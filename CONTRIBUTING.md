# Contributing

Keep changes focused and include a regression test for switching, storage, process handling, or recovery behavior. Run:

```powershell
dotnet build Vaguul.CodexAccountSwitcher.sln -c Release -warnaserror
dotnet run --project tests/Vaguul.CodexAccountSwitcher.Tests -c Release
```

Use synthetic authentication JSON only. Never commit or attach a real credential file, token, account identifier, or user profile path.
