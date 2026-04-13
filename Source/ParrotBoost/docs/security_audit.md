# Security Audit: ParrotBoost (JGS)

## 👤 Administrator Privileges
- Application requires **Administrator** privileges to:
    - Stop/Disable system services.
    - Change power plans.
    - Delete files from system folders (Prefetch, Temp).
    - Modify system registry keys.

## 📂 File Access
- The application only accesses the following folders:
    - `%PROGRAMDATA%\JGS\logs\` (for logging).
    - `C:\Windows\Prefetch\` (for cleanup).
    - `%USERPROFILE%\AppData\Local\Temp\` (for cleanup).
    - `C:\Windows\Temp\` (for cleanup).

## 🛠 Commands and Scripts
- Commands are executed via `ProcessStartInfo` with `Hidden` window style.
- PowerShell scripts are run via `powershell -Command "..."`.
- All commands are hardcoded and not subject to user input injection.

## 🛡 Registry Access
- Registry access is limited to:
    - `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects`
    - `HKEY_CURRENT_USER\Control Panel\Desktop`
    - Power plan configuration via `powercfg`.
