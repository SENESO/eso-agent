using System.Text.Json;

namespace EsoAgent.Tray;

/// <summary>
/// The tray's string table (UI Phase 5): every user-visible tray, consent,
/// host-window and prompt string in Arabic and English, picked by one
/// persisted preference (tray-settings.json in the data dir, default
/// Arabic — the owner's language). Before this, the tray menu was
/// Arabic-only, the consent prompt English-only, and the password prompt
/// mixed both on one form.
/// </summary>
public static class TrayStrings
{
    private static string _settingsPath = "";

    public static bool IsArabic { get; private set; } = true;

    public static string T(string en, string ar) => IsArabic ? ar : en;

    public static void Load(string dataDir)
    {
        _settingsPath = Path.Combine(dataDir, "tray-settings.json");
        try
        {
            if (File.Exists(_settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                if (doc.RootElement.TryGetProperty("language", out var lang))
                    IsArabic = lang.GetString() != "en";
            }
        }
        catch { /* a torn settings file falls back to Arabic */ }
    }

    public static void ToggleLanguage()
    {
        IsArabic = !IsArabic;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, $"{{\"language\":\"{(IsArabic ? "ar" : "en")}\"}}");
        }
        catch { /* the choice still applies for this run */ }
    }

    /// <summary>The language menu item always names the OTHER language.</summary>
    public static string OtherLanguageName => IsArabic ? "English" : "العربية";

    // ----- tray menu / status -----
    public static string StatusUnknown => T("Status: …", "الحالة: …");
    public static string StatusControlled => T("Status: being controlled now", "الحالة: يتم التحكم الآن");
    public static string StatusLocked => T("Status: locked (kill switch)", "الحالة: مقفول (kill switch)");
    public static string StatusConnected => T("Status: connected", "الحالة: متصل");
    public static string StatusStandalone => T("Status: running (no fleet)", "الحالة: يعمل (بدون أسطول)");
    public static string StatusServiceDown => T("Service not connected", "الخدمة غير متصلة");
    public static string UpdateAvailableSuffix(string version) => T($" — update available: v{version}", $" — تحديث متاح: v{version}");
    public static string LastCommand(string? command) => T($"Last command: {command ?? "—"}", $"آخر أمر: {command ?? "—"}");
    public static string DeviceLine(string name, string? code) => code != null
        ? T($"Device: {name} ({code}) — click copies the code", $"الجهاز: {name} ({code}) — انسخ الكود")
        : T($"Device: {name}", $"الجهاز: {name}");
    public static string MeshLine(string? ip) => ip != null
        ? T($"Network address: {ip} — click copies", $"عنوان الشبكة: {ip} — انسخ")
        : T("Network address: —", "عنوان الشبكة: —");
    public static string OwnerActive => T("Owner: dashboard session active", "المالك: جلسة لوحة نشطة");
    public static string OwnerIdle => T("Owner: no active session", "المالك: لا جلسة نشطة");
    public static string CreateAccessCode => T("Create access code…", "إنشاء كود وصول…");
    public static string OpenApp => T("Open the app", "فتح التطبيق");
    public static string OpenHostWindow => T("This device (host window)", "هذا الجهاز (نافذة المضيف)");
    public static string ViewLog => T("View the log", "عرض السجل");
    public static string OpenDashboard => T("Open the dashboard", "فتح لوحة التحكم");
    public static string Exit => T("Exit", "خروج");
    public static string LockNow => T("Disconnect (kill switch)", "قطع الاتصال");
    public static string Resume => T("Resume connection", "استئناف الاتصال");
    public static string TooltipControlled => T("EsoAgent — this device is being controlled now", "EsoAgent — يتم التحكم في هذا الجهاز الآن");
    public static string TooltipLocked => T("EsoAgent — locked (kill switch)", "EsoAgent — مقفول (kill switch)");
    public static string TooltipConnected => T("EsoAgent — connected", "EsoAgent — متصل");
    public static string TooltipStandalone => T("EsoAgent — running", "EsoAgent — يعمل");
    public static string TooltipDown => T("EsoAgent — service not connected", "EsoAgent — الخدمة غير متصلة");

    // ----- prompts / messages -----
    public static string WrongPassword => T("Wrong owner password — connection not resumed.", "كلمة مرور المالك غير صحيحة — لم يتم الاستئناف.");
    public static string ServiceUnreachable => T("Could not reach the EsoAgent service.", "تعذر الاتصال بخدمة EsoAgent.");
    public static string LogMissing(string path) => T($"The log file does not exist:\n{path}", $"ملف السجل غير موجود:\n{path}");
    public static string PasswordPromptTitle => T("EsoAgent — Owner password", "EsoAgent — كلمة مرور المالك");
    public static string PasswordPromptLabel => T("Resuming the connection requires the owner password:", "استئناف الاتصال يتطلب كلمة مرور المالك:");
    public static string PasswordPromptButton => T("Resume", "استئناف");

    // ----- consent v2 -----
    public static string ConsentTitle => T("EsoAgent — remote session request", "EsoAgent — طلب جلسة عن بُعد");

    /// <summary>Translates the Core requester labels for the Arabic prompt;
    /// unknown labels pass through unchanged.</summary>
    public static string Requester(string requester) => !IsArabic ? requester : requester switch
    {
        "the operator console" => "وحدة تحكم المشغّل",
        "the fleet owner panel" => "لوحة مالك الأسطول",
        "the owner dashboard on this device" => "لوحة المالك على هذا الجهاز",
        "a direct connection (the EsoAgent app or CLI)" => "اتصال مباشر (تطبيق EsoAgent أو سطر الأوامر)",
        "the fleet server" => "خادم الأسطول",
        "a remote operator" => "مشغّل عن بُعد",
        _ => requester,
    };

    public static string ConsentQuestion(string requester, bool control) => control
        ? T($"{Capitalize(requester)} is asking to CONTROL this computer.", $"{Requester(requester)} يطلب التحكم في هذا الكمبيوتر.")
        : T($"{Capitalize(requester)} is asking to VIEW this computer's screen.", $"{Requester(requester)} يطلب رؤية شاشة هذا الكمبيوتر.");

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string PermScreen => T("✓  Can see your screen", "✓  يمكنه رؤية شاشتك");
    public static string PermInputYes => T("✓  Can use your mouse and keyboard", "✓  يمكنه استخدام الفأرة ولوحة المفاتيح");
    public static string PermInputNo => T("✕  Cannot use your mouse or keyboard", "✕  لا يمكنه استخدام الفأرة ولوحة المفاتيح");
    public static string PermClipboardYes => T("✓  Can sync the clipboard", "✓  يمكنه مزامنة الحافظة");
    public static string PermClipboardNo => T("✕  No clipboard sync", "✕  بدون مزامنة للحافظة");
    public static string ConsentExpires(int seconds) => T(
        $"This request expires in {seconds}s — no answer means No.",
        $"ينتهي هذا الطلب خلال {seconds} ثانية — عدم الرد يعني الرفض.");
    public static string Allow => T("Allow", "سماح");
    public static string Deny => T("Deny", "رفض");

    // ----- host window -----
    public static string HostTitle => T("EsoAgent — this device", "EsoAgent — هذا الجهاز");
    public static string DeviceCodeLabel => T("Device code (give this to whoever connects)", "كود الجهاز (اعطه لمن سيتصل)");
    public static string Copy => T("Copy", "نسخ");
    public static string DeviceNameLabel => T("Device name", "اسم الجهاز");
    public static string MeshLabel => T("Network (mesh) address", "عنوان الشبكة (Mesh)");
    public static string StatusLabel => T("Status", "الحالة");
    public static string UpdateReady(string version) => T($"Update available: v{version}", $"تحديث متاح: v{version}");
    public static string ApplyUpdate => T("Apply the update", "تطبيق التحديث");
    public static string NoUpdate => T("No update available", "لا يوجد تحديث متاح");
    public static string Applying => T("Applying…", "جارٍ التطبيق…");
    public static string Close => T("Close", "إغلاق");

    // ----- log viewer -----
    public static string LogTitle => T("EsoAgent — audit log", "EsoAgent — سجل التدقيق");
    public static string Refresh => T("Refresh", "تحديث");
    public static string Follow => T("Follow new lines", "متابعة الأسطر الجديدة");
}
