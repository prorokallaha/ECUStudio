using ECUStudio.Application.Analysis;
using ECUStudio.Application.DevTools;
using ECUStudio.Core;
using ECUStudio.Simulation;
using ECUStudio.Simulation.Logs;

namespace ECUStudio.Tests;

public class LogParserTests
{
    [Fact]
    public void Parses_vcds_layout_with_qualifier_and_unit_rows()
    {
        const string csv = """
            Wednesday,07,October,2026,12:00:00:00000,VCID:1234
            Address 01: Engine,Labels: 038-906-016-BKC.lbl
            ,TIME,Engine Speed,Air Mass,Air Mass,Boost Pressure,Boost Pressure
            Marker,STAMP,,Specified,Actual,Specified,Actual
            ,s,/min,mg/str,mg/str,mbar,mbar
            ,0.00,1500,500,498,1900,1850
            ,0.25,1600,520,515,2000,1990
            ,0.50,1700,540,530,2100,2080
            """;
        var log = LogParser.Parse("vcds.csv", csv);
        Assert.Equal("VCDS", log.Format);
        Assert.Equal(3, log.SampleCount);
        Assert.Equal([1500.0, 1600, 1700], log[LogChannel.Rpm]!);
        Assert.Equal([498.0, 515, 530], log[LogChannel.MafActual]!);
        Assert.Equal([500.0, 520, 540], log[LogChannel.MafSpecified]!);
        Assert.Equal([1850.0, 1990, 2080], log[LogChannel.BoostActual]!);
        Assert.Equal([1900.0, 2000, 2100], log[LogChannel.BoostSpecified]!);
        Assert.True(log.Has(LogChannel.Time));
    }

    [Fact]
    public void Parses_semicolon_csv_with_decimal_comma_units_in_values_and_bar()
    {
        const string csv = """
            Motordrehzahl;Ladedruck Istwert [bar];Einspritzmenge
            1500 /min;1,85;20,5
            2000 /min;2,10;31,0
            """;
        var log = LogParser.Parse("de.csv", csv);
        Assert.Equal("CSV", log.Format);
        Assert.Equal([1500.0, 2000], log[LogChannel.Rpm]!);
        Assert.Equal(1850, log[LogChannel.BoostActual]![0], 6);
        Assert.Equal(2100, log[LogChannel.BoostActual]![1], 6);
        Assert.Equal([20.5, 31.0], log[LogChannel.IqActual]!);
    }

    [Fact]
    public void Ambiguous_units_drop_the_channel_with_a_warning()
    {
        var log = LogParser.Parse("psi.csv", "rpm,boost (psi),maf\n2000,15,600\n2500,17,700\n");
        Assert.False(log.Has(LogChannel.BoostActual));
        Assert.Contains(log.Warnings, w => w.Contains("psi"));
        Assert.True(log.Has(LogChannel.MafActual));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a,b,c\n1,2,3\n")]
    [InlineData("boost,maf\n2000,600\n2100,650\n")]
    [InlineData("rpm,maf\n")]
    public void Unusable_logs_are_rejected_with_a_domain_error(string csv)
    {
        var ex = Assert.Throws<LogFormatException>(() => LogParser.Parse("x.csv", csv));
        Assert.Equal(422, ex.HttpStatus);
    }

    [Theory]
    [InlineData("Engine Speed", LogChannel.Rpm)]
    [InlineData("liquid level", null)]
    [InlineData("boost pressure control duty", null)]
    [InlineData("intake air temperature", LogChannel.IntakeTemp)]
    [InlineData("injection quantity limitation smoke", null)]
    [InlineData("injection quantity requested", LogChannel.IqRequested)]
    public void Classification_avoids_false_matches(string header, LogChannel? expected)
    {
        Assert.Equal(expected, LogParser.Classify(header));
    }

    [Fact]
    public void Latin1_vcds_export_is_decoded()
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes("Kühlmitteltemperatur");
        Assert.Equal("Kühlmitteltemperatur", LogParser.Decode(bytes));
    }
}

public class LogValidationTests
{
    private static readonly Lazy<AnalysisSession> Session = new(() => Fixtures.Pipeline().Run(new AnalysisRequest
    {
        Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"),
        Stock = Fixtures.Image(Fixtures.Stock, "stock.bin"),
        Vin = Fixtures.GolfVin,
        Grid = Fixtures.FastGrid,
    }));

    private static DiagnosticLog Synthetic(double offsetPct) =>
        LogParser.Parse("pull.csv", SyntheticLog.GenerateVcdsCsv(Session.Value.ModInput, new SimulationEngine(), boostOffsetPct: offsetPct));

    private static AnalysisReport Run(params LogInput[] logs) => Fixtures.Pipeline().Run(new AnalysisRequest
    {
        Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"),
        Stock = Fixtures.Image(Fixtures.Stock, "stock.bin"),
        Vin = Fixtures.GolfVin,
        Grid = Fixtures.FastGrid,
        Logs = logs,
    }).Report;

    [Fact]
    public void Full_load_samples_are_selected_by_pedal()
    {
        var log = Synthetic(0);
        var (wot, criterion) = LogValidator.SelectWot(log);
        Assert.Contains("pedal", criterion);
        Assert.True(wot.Count >= LogValidator.MinWotSamples);
        Assert.All(wot, i => Assert.True(log[LogChannel.Pedal]![i] >= 95));
    }

    [Fact]
    public void Agreeing_log_raises_data_availability()
    {
        var without = Run();
        var with = Run(new LogInput("l1", Synthetic(-2.5), false));
        var v = Assert.Single(with.Logs);
        Assert.Equal(LogAgreement.Agrees, v.Status);
        Assert.True(with.DataAvailability.Score > without.DataAvailability.Score);
        Assert.Contains(with.DataAvailability.Factors, f => f.Contains("agrees"));
        var boost = v.Channels.Single(c => c.Channel == LogChannel.BoostActual);
        Assert.InRange(boost.BiasPct!.Value, -8, 8);
        Assert.InRange(v.AtmosphericPressureMbar!.Value, 985, 995);
    }

    [Fact]
    public void Deviating_log_is_reported_and_does_not_raise_confidence()
    {
        var without = Run();
        var with = Run(new LogInput("l2", Synthetic(-20), false));
        var v = Assert.Single(with.Logs);
        Assert.Equal(LogAgreement.Deviates, v.Status);
        Assert.Equal(without.DataAvailability.Score, with.DataAvailability.Score);
        var f = Assert.Single(with.CalibrationFindings, x => x.Code == "MODEL_LOG_MISMATCH");
        Assert.Equal("log:l2:BoostActual", Assert.Single(f.Evidence).Ref);
        Assert.Contains("log:l2:BoostActual", AIContextBuilder.Build(Fixtures.Pipeline().Run(new AnalysisRequest
        {
            Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"), Grid = Fixtures.FastGrid, Logs = [new LogInput("l2", Synthetic(-20), false)],
        })).AllowedRefs);
    }

    [Fact]
    public void Part_load_only_log_is_insufficient()
    {
        var csv = "rpm,pedal,boost (mbar)\n" + string.Join('\n', Enumerable.Range(0, 50).Select(i => $"{1500 + i * 10},30,{1200 + i}"));
        var report = Run(new LogInput("l3", LogParser.Parse("cruise.csv", csv), false));
        var v = Assert.Single(report.Logs);
        Assert.True(v.Status == LogAgreement.Insufficient, $"{v.Status} {v.WotSamples} {string.Join(';', report.DataAvailability.Factors)}");
        Assert.Contains(report.DataAvailability.Factors, f => f.Contains("without enough full-load"));
    }

    [Fact]
    public void Stock_log_is_skipped_when_no_stock_file_is_analysed()
    {
        var report = Fixtures.Pipeline().Run(new AnalysisRequest
        {
            Modified = Fixtures.Image(Fixtures.Stage1, "stage1.bin"), Grid = Fixtures.FastGrid, Logs = [new LogInput("s", Synthetic(0), true)],
        }).Report;
        Assert.Empty(report.Logs);
    }
}
