# Agent Instructions

Before starting any task, read and follow [.github/copilot-instructions.md](.github/copilot-instructions.md). It holds the full project context and coding standards.

<!-- foundation-documentation-sources:start -->
## Documentation Sources

For documentation, MUST use first-party vendor MCPs first (Microsoft Learn for Microsoft technologies). Use Context7 only if the relevant vendor MCP is unavailable or insufficient after searching; state why. This overrides generic Context7 triggers.
<!-- foundation-documentation-sources:end -->

<!-- dotnet-backward-compatible-renames:start -->
## Backward-Compatible Renames
When you rename or restructure, keep every name that leaves the repository working: serialized names, database names, configuration keys, routes and other public contracts. The only exception is a request that names this directive and says to skip it. Follow the `dotnet-refactoring` skill for both cases. A name that only this repository's code uses can change freely.
<!-- dotnet-backward-compatible-renames:end -->
