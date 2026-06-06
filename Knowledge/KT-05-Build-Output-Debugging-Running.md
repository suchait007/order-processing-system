# KT-05: .NET Build Output, Debugging & Running — Java Developer's Guide
**Author:** Copilot KT Session  
**Created:** 2026-06-05  
**Audience:** Java developers transitioning to .NET  
**Status:** Living Document

---

## Table of Contents
1. [Java vs .NET — Build Output Comparison](#1-java-vs-net--build-output-comparison)
2. [Build Output Folder Structure](#2-build-output-folder-structure)
3. [How .NET Apps Run](#3-how-net-apps-run)
4. [Publishing — Single File & Self-Contained](#4-publishing--single-file--self-contained)
5. [Visual Studio Run Modes](#5-visual-studio-run-modes)
6. [Visual Studio Debug Output — Reducing Noise](#6-visual-studio-debug-output--reducing-noise)
7. [HTTPS & Development Certificates](#7-https--development-certificates)
8. [Ports & Launch Profiles](#8-ports--launch-profiles)

---

## 1. Java vs .NET — Build Output Comparison

| Concept | Java (IntelliJ) | .NET (Visual Studio) |
|---|---|---|
| Compiled output | `.jar` / `.war` | `.dll` (always a DLL, even for executables) |
| Executable | `java -jar app.jar` | `app.exe` (thin wrapper) + `app.dll` (actual code) |
| Build output folder | `target/` | `bin/Debug/net8.0/` or `bin/Release/net8.0/` |
| Intermediate files | `target/classes/` | `obj/` (ignore this folder) |
| Package manager cache | `~/.m2/repository/` | `~/.nuget/packages/` |
| Dependency descriptor | `pom.xml` / `build.gradle` | `.csproj` |
| Lock file | `pom.xml` (implicit) | `packages.lock.json` or `paket.lock` |
| Bytecode format | `.class` files (JVM bytecode) | `.dll` files (CIL / IL bytecode) |
| Runtime | JRE / JDK | .NET Runtime (`C:\Program Files\dotnet\`) |
| Fat jar equivalent | `maven-shade-plugin` / Spring Boot fat jar | `dotnet publish --self-contained /p:PublishSingleFile=true` |

---

## 2. Build Output Folder Structure

After running `dotnet build` or pressing F5/Ctrl+F5 in Visual Studio:

```
StoreApi/
├── bin/                              ← Build output (like target/ in Java)
│   └── Debug/                        ← Configuration (Debug or Release)
│       └── net8.0/                   ← Target framework
│           ├── StoreApi.exe          ← Thin native launcher (like java.exe wrapper)
│           ├── StoreApi.dll          ← YOUR compiled code (this IS your "jar")
│           ├── StoreApi.pdb          ← Debug symbols (like .class debug info)
│           ├── StoreApi.deps.json    ← Dependency graph (what DLLs are needed)
│           ├── StoreApi.runtimeconfig.json  ← Runtime configuration (framework version)
│           ├── appsettings.json      ← Copied config files
│           ├── Serilog.dll           ← NuGet dependencies copied here
│           ├── Microsoft.EntityFrameworkCore.dll
│           ├── Yuniql.Core.dll
│           ├── db/                   ← Yuniql migrations (copied to output)
│           └── logs/                 ← Log files created at runtime
│
├── obj/                              ← Intermediate build artifacts (IGNORE)
│   └── Debug/
│       └── net8.0/
│           ├── StoreApi.dll          ← Intermediate compilation output
│           ├── StoreApi.csproj.nuget.g.props  ← NuGet restore info
│           └── ...                   ← Build cache, assembly info, etc.
│
└── StoreApi.csproj                   ← Project file (like pom.xml)
```

### Key Points

- **`bin/`** = Final output. This is what you deploy.
- **`obj/`** = Build cache. Never deploy this. Like `target/classes/` in Maven.
- **`StoreApi.dll`** = Your compiled code. Despite the `.dll` extension, this IS the application.
- **`StoreApi.exe`** = A thin native launcher that calls `dotnet StoreApi.dll`. It's ~150KB.
- **Dependencies** are separate DLL files alongside your app (not bundled like a fat jar).

### The "DLL" Confusion for Java Developers

In Java: `.jar` = your app, `.jar` also = libraries.  
In .NET: `.dll` = your app, `.dll` also = libraries.  

There's no `.exe` with all your code in it. The `.exe` is just a bootstrapper:

```
StoreApi.exe → calls → dotnet runtime → loads → StoreApi.dll → runs your code
```

---

## 3. How .NET Apps Run

### Three Ways to Run

```bash
# Method 1: Using dotnet CLI (like java -jar)
dotnet StoreApi.dll

# Method 2: Using the generated exe (just a shortcut for Method 1)
StoreApi.exe

# Method 3: During development (builds + runs)
dotnet run
```

### Java Comparison

```bash
# Java
java -jar target/StoreApi.jar

# .NET equivalent
dotnet bin/Debug/net8.0/StoreApi.dll
```

### Shared Runtime vs Self-Contained

.NET apps require the .NET Runtime installed on the machine (like needing a JRE):

```
Shared Runtime (default):
  Machine has: C:\Program Files\dotnet\  (shared .NET runtime)
  Your app:    StoreApi.dll (just YOUR code, ~50KB)
  Deps:        Serilog.dll, EF Core.dll, etc. (~5MB)
  Total:       ~5MB

Self-Contained (bundled):
  Your app:    StoreApi.exe (YOUR code + entire .NET runtime)
  Total:       ~80-150MB (like a fat jar with JRE bundled)
```

---

## 4. Publishing — Single File & Self-Contained

### Framework-Dependent (default) — Requires .NET Runtime on Target

```bash
dotnet publish -c Release
# Output: bin/Release/net8.0/publish/
# Small output, but target machine needs .NET 8 runtime installed
```

### Self-Contained — No Runtime Needed on Target

```bash
dotnet publish -c Release --self-contained -r win-x64
# Output: bin/Release/net8.0/win-x64/publish/
# Larger output (~80MB), but runs on any Windows x64 machine
```

### Single File (Fat Jar Equivalent)

```bash
dotnet publish -c Release --self-contained -r win-x64 /p:PublishSingleFile=true
# Output: One single StoreApi.exe (~80MB)
# Everything bundled: your code + runtime + dependencies
```

### Single File + Trimmed (Smallest Possible)

```bash
dotnet publish -c Release --self-contained -r win-x64 /p:PublishSingleFile=true /p:PublishTrimmed=true
# Output: One StoreApi.exe (~30-50MB)
# Dead code removed (like ProGuard in Java)
```

### Comparison Table

| Mode | Size | Needs Runtime? | Files | Java Equivalent |
|---|---|---|---|---|
| Framework-dependent | ~5MB | ✅ Yes | Many DLLs | `app.jar` (needs JRE) |
| Self-contained | ~80MB | ❌ No | Many DLLs | `app.jar` + bundled JRE |
| Single file | ~80MB | ❌ No | One `.exe` | Fat jar with embedded JRE |
| Single file + trimmed | ~30-50MB | ❌ No | One `.exe` | Fat jar + ProGuard |

---

## 5. Visual Studio Run Modes

### F5 — Start Debugging (like IntelliJ Debug)

| Feature | Behavior |
|---|---|
| Debugger | ✅ Attached |
| Breakpoints | ✅ Work |
| Module load messages | ❌ Noisy ("Loaded System.Runtime.dll...") |
| Thread exit messages | ❌ Noisy ("Thread has exited...") |
| Performance | Slower (debugger overhead) |
| Hot Reload | ✅ Available |

### Ctrl+F5 — Start Without Debugging (like IntelliJ Run)

| Feature | Behavior |
|---|---|
| Debugger | ❌ Not attached |
| Breakpoints | ❌ Don't work |
| Module load messages | ✅ None |
| Thread exit messages | ✅ None |
| Performance | Full speed |
| Console output | Clean — only your Serilog logs |

### Menu Access

```
Debug → Start Debugging           (F5)
Debug → Start Without Debugging   (Ctrl+F5)
```

### Recommendation

- **`Ctrl+F5`** for normal development (clean console output, full speed)
- **`F5`** only when you need to hit breakpoints

---

## 6. Visual Studio Debug Output — Reducing Noise

When running with F5 (Debug), the Output window shows assembly loading and thread messages:

```
'StoreApi.exe' (CoreCLR): Loaded 'System.Private.CoreLib.dll'. Skipped loading symbols.
'StoreApi.exe' (CoreCLR): Loaded 'System.Runtime.dll'. Skipped loading symbols.
The thread '.NET TP Worker' (39192) has exited with code 0.
```

### To Suppress These

Go to **Tools → Options → Debugging → General**:

| Setting | Action |
|---|---|
| ☐ Enable Module Load Messages | **Uncheck** — stops DLL loading messages |
| ☐ Log Thread Exit Messages | **Uncheck** — stops thread exit messages |
| ☑ Enable Just My Code | Keep checked — skips framework code in debugger |

After these changes, the Debug output will only show your application logs.

**Note:** These messages are **only in Visual Studio**. Your log files (`logs/storeapi-*.log`) are always clean.

---

## 7. HTTPS & Development Certificates

When you create a new ASP.NET Core project, it configures HTTPS by default:

- `app.UseHttpsRedirection()` in Program.cs redirects HTTP → HTTPS
- On first debug run, Visual Studio prompts to **trust a self-signed dev certificate**

### Managing the Dev Certificate

```bash
dotnet dev-certs https --trust    # Trust the certificate (one-time)
dotnet dev-certs https --clean    # Remove the certificate
dotnet dev-certs https --check    # Check if certificate exists
```

### If You Don't Want HTTPS Locally

Remove `app.UseHttpsRedirection()` from Program.cs and use the `http` launch profile.

---

## 8. Ports & Launch Profiles

### No Fixed Default Port

Each new project gets a **randomly assigned port** during scaffolding, stored in:

```
Properties/launchSettings.json
```

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5116",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    },
    "https": {
      "commandName": "Project",
      "applicationUrl": "https://localhost:7075;http://localhost:5116"
    },
    "IIS Express": {
      "commandName": "IISExpress",
      "applicationUrl": "http://localhost:47413",
      "sslPort": 44303
    }
  }
}
```

### Port Ranges

| Profile | Port Range | Example |
|---|---|---|
| `http` | 5000–5300 | `http://localhost:5116` |
| `https` | 7000–7300 | `https://localhost:7075` |
| IIS Express | Random high port | `http://localhost:47413` |

### Without launchSettings.json

Kestrel defaults to **port 5000 (HTTP)** and **5001 (HTTPS)**.

### Choosing Profile in Visual Studio

In the toolbar, there's a **dropdown next to the green play button (▶)**:

```
▶  IIS Express  ▼     ← Logs go to VS Output window (often noisy)
▶  http         ▼     ← Logs go to console window (clean)
▶  https        ▼     ← Same as http but with HTTPS
```

**Recommendation:** Use **`http`** or **`https`** profiles, not IIS Express.

### Java Comparison

| Concept | Java (IntelliJ) | .NET (Visual Studio) |
|---|---|---|
| Port config | `application.properties` / `application.yml` | `launchSettings.json` |
| Default port | 8080 (Spring Boot) | Random (no fixed default) |
| Environment | `spring.profiles.active=dev` | `ASPNETCORE_ENVIRONMENT=Development` |
| Run config | IntelliJ Run Configurations | Launch Profiles in dropdown |

---

## References
- [dotnet publish documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish)
- [Single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file)
- [Launch profiles](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/environments)
- [Development certificates](https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl)
