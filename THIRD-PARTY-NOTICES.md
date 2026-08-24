# Third-party notices

DNA X is licensed under the [MIT License](LICENSE). It depends on the
following third-party packages, each under its own license:

## Runtime dependencies

- **Microsoft.Extensions.DependencyInjection.Abstractions**,
  **Microsoft.Extensions.FileProviders.Abstractions**,
  **Microsoft.Extensions.Hosting.Abstractions**,
  **Microsoft.Extensions.Logging.Abstractions**,
  **Microsoft.Extensions.Options**, **Microsoft.Extensions.DependencyInjection**,
  and **Microsoft.Data.Sqlite** — MIT License,
  © Microsoft Corporation. <https://github.com/dotnet/runtime>
- **SQLite** (used by the opt-in `DnaX.Data.Migrations.Sqlite` package) —
  public domain. <https://www.sqlite.org/copyright.html>
- **StackExchange.Redis** (used only by `DnaX.Redis.StackExchangeRedis`) —
  MIT License, © Stack Exchange, Inc.
  <https://github.com/StackExchange/StackExchange.Redis>

## Test-only dependencies

- **xunit**, **xunit.runner.visualstudio** — Apache License 2.0.
  <https://github.com/xunit/xunit>
- **Microsoft.NET.Test.Sdk** — MIT License, © Microsoft Corporation.
  <https://github.com/microsoft/vstest>

Applications that use `DnaX.Data` or a provider-specific migration adapter
supply their own ADO.NET provider (for example Npgsql, Microsoft.Data.SqlClient,
or Oracle.ManagedDataAccess.Core); those providers retain their respective
licenses and are never redistributed by DNA X.
