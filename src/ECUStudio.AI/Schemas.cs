using System.Text.Json.Nodes;

namespace ECUStudio.AI;

/// <summary>JSON schemas for structured outputs. Every claim must carry evidence references.</summary>
public static class Schemas
{
    private static JsonObject Str() => new() { ["type"] = "string" };
    private static JsonObject Num() => new() { ["type"] = "number" };
    private static JsonObject StrArr() => new() { ["type"] = "array", ["items"] = Str() };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    private static JsonObject Obj(JsonObject props) => new()
    {
        ["type"] = "object",
        ["properties"] = props,
        ["required"] = new JsonArray(props.Select(p => (JsonNode)p.Key).ToArray()),
        ["additionalProperties"] = false,
    };

    public static readonly string[] Severities = ["SAFE", "REVIEW", "WARNING", "DANGER", "UNKNOWN"];

    public static JsonObject Evidence() => Obj(new JsonObject
    {
        ["type"] = Enum("MAP", "DIFF", "SIMULATION", "COMPONENT", "KNOWLEDGE_BASE", "RULE", "LOG", "BINARY"),
        ["ref"] = Str(),
        ["detail"] = Str(),
    });

    public static JsonObject Finding() => Obj(new JsonObject
    {
        ["finding"] = Str(),
        ["severity"] = Enum(Severities),
        ["confidence"] = Num(),
        ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = Evidence() },
        ["assumptions"] = StrArr(),
        ["unknowns"] = StrArr(),
        ["affected_components"] = StrArr(),
        ["related_maps"] = StrArr(),
    });

    public static JsonObject Findings() => Obj(new JsonObject
    {
        ["findings"] = new JsonObject { ["type"] = "array", ["items"] = Finding() },
        ["missing_data"] = StrArr(),
    });

    public static JsonObject SafetyReview() => Obj(new JsonObject
    {
        ["verdict"] = Enum(Severities),
        ["confidence"] = Num(),
        ["contradictions"] = StrArr(),
        ["findings"] = new JsonObject { ["type"] = "array", ["items"] = Finding() },
        ["missing_data"] = StrArr(),
    });

    public static JsonObject Verification() => Obj(new JsonObject
    {
        ["verdict"] = Enum("SUPPORTED", "CONTRADICTED", "INSUFFICIENT"),
        ["confidence"] = Num(),
        ["rationale"] = Str(),
        ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = Evidence() },
    });

    public static JsonObject MapHypotheses(IEnumerable<string> roles) => Obj(new JsonObject
    {
        ["hypotheses"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = Obj(new JsonObject
            {
                ["role"] = Enum(roles.ToArray()),
                ["confidence"] = Num(),
                ["rationale"] = Str(),
                ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = Evidence() },
            }),
        },
        ["data_needed"] = StrArr(),
    });

    public static JsonObject MapInvestigation(IEnumerable<string> roles) => Obj(new JsonObject
    {
        ["purpose"] = Enum(roles.ToArray()),
        ["purpose_text"] = Str(),
        ["confidence"] = Num(),
        ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = Evidence() },
        ["counter_evidence"] = StrArr(),
        ["value_unit"] = Str(),
        ["x_axis"] = Str(),
        ["y_axis"] = Str(),
        ["related_maps"] = StrArr(),
        ["verification_steps"] = StrArr(),
        ["alternatives"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = Obj(new JsonObject { ["purpose"] = Enum(roles.ToArray()), ["confidence"] = Num(), ["rationale"] = Str() }),
        },
    });

    public static JsonObject AssistantAnswer() => Obj(new JsonObject
    {
        ["answer"] = Str(),
        ["confidence"] = Num(),
        ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = Evidence() },
        ["assumptions"] = StrArr(),
        ["unknowns"] = StrArr(),
        ["suggested_checks"] = StrArr(),
    });
}
