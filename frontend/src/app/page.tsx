import { Suspense } from "react";
import { ProjectsPage } from "@/features/projects/projects-page";

export default function Home() {
  return <Suspense><ProjectsPage /></Suspense>;
}
