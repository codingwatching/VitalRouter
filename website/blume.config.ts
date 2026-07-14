import { defineConfig } from "blume";

export default defineConfig({
  title: "VitalRouter",
  description:
    "A source-generator powered zero-allocation fast in-memory messaging library for Unity and .NET.",
  logo: {
    image: {
      light: "/img/logo2.svg",
      dark: "/img/logo2_dark.svg",
      alt: "VitalRouter",
    },
    text: "",
    href: "/",
  },
  github: {
    owner: "hadashiA",
    repo: "VitalRouter",
    branch: "main",
    dir: "website",
  },
  navigation: {
    sidebar: [
      "/",
      {
        label: "Getting Started",
        collapsed: false,
        items: [
          "/getting-started/installation",
          "/getting-started/icommand",
          "/getting-started/declarative-routing-pattern",
          "/getting-started/event-handler-pattern",
        ],
      },
      {
        label: "Dependency Injection (DI)",
        collapsed: false,
        items: ["/di/intro", "/di/vcontainer", "/di/microsoft-extensions"],
      },
      {
        label: "Pipeline",
        collapsed: false,
        items: [
          "/pipeline/interceptor",
          "/pipeline/sequential-control",
          "/pipeline/publish-context",
          {
            label: "Built-in interceptors",
            collapsed: false,
            items: ["/pipeline/command-pooling", "/pipeline/fan-out"],
          },
        ],
      },
      {
        label: "Extensions",
        collapsed: false,
        items: [
          "/extensions/unitask",
          "/extensions/r3",
          {
            label: "MRuby Scripting",
            collapsed: false,
            items: [
              "/extensions/mruby/intro",
              "/extensions/mruby/v2",
              "/extensions/mruby/v1",
            ],
          },
        ],
      },
    ],
  },
  deployment: {
    output: "static",
    site: "https://vitalrouter.hadashikick.jp",
  },
});
