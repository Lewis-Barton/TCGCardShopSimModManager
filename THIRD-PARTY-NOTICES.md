# Third-party notices

This project uses the following third-party libraries.

| Package | Purpose | License |
| --- | --- | --- |
| [Avalonia](https://avalonUI.net) + Avalonia.Desktop + Avalonia.Themes.Fluent | Cross-platform desktop UI framework | MIT |
| [Tmds.DBus.Protocol](https://www.nuget.org/packages/Tmds.DBus.Protocol) | Linux DBus support pulled in by Avalonia (not used on Windows) | MIT |
| [System.Security.Cryptography.ProtectedData](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData) | DPAPI encryption of the stored Nexus API key | MIT |
| [SharpCompress](https://www.nuget.org/packages/SharpCompress) | RAR, 7Z, TAR and compressed-stream archive reading | MIT |
| [xunit](https://xunit.net) + xunit.runner.visualstudio | Test framework (development only) | Apache-2.0 |
| Microsoft.NET.Test.Sdk / coverlet.collector | Test runner + coverage (development only) | MIT |

Licenses are available at the linked project homepages and from NuGet. Release
builds include the runtime libraries needed by the application inside the
self-contained executables.

Unrelated to software: **TCG Card Shop Simulator** is a game, not a library.
This tool is not affiliated with its developer and distributes no game assets.
