namespace EsoAgent.App;

/// <summary>
/// EN/AR strings for the App's main screens (v1.2, item 17). Arabic is a
/// first-class audience for this product, not an afterthought: the toggle
/// lives in the main window header, persists across runs, and flips the
/// window's flow direction (RTL) in Arabic. Deep dialogs keep English
/// technical terms where a translation would mislead (paths, codes, hashes).
/// </summary>
public static class Strings
{
    public static string Language { get; set; } = "en";   // "en" | "ar"
    public static bool IsArabic => Language == "ar";

    private static readonly Dictionary<string, (string En, string Ar)> Table = new()
    {
        ["Status"] = ("Status", "الحالة"),
        ["Desktop"] = ("Desktop", "سطح المكتب"),
        ["Terminal"] = ("Terminal", "الطرفية"),
        ["Files"] = ("Files", "الملفات"),
        ["Activity"] = ("Activity", "النشاط"),
        ["Access"] = ("Access", "الوصول"),
        ["RefreshDevices"] = ("Refresh devices", "تحديث الأجهزة"),
        ["Settings"] = ("Settings", "الإعدادات"),
        ["OpenInBrowser"] = ("Open in browser", "افتح في المتصفح"),
        ["Refresh"] = ("Refresh", "تحديث"),
        ["ExportJson"] = ("Export JSON", "تصدير JSON"),
        ["Upload"] = ("Upload…", "رفع…"),
        ["Download"] = ("Download…", "تنزيل…"),
        ["Delete"] = ("Delete", "حذف"),
        ["Rename"] = ("Rename…", "إعادة تسمية…"),
        ["NewFolder"] = ("New folder…", "مجلد جديد…"),
        ["Up"] = ("↑ Up", "↑ لأعلى"),
        ["Connect"] = ("Connect", "اتصال"),
        ["Disconnect"] = ("Disconnect", "قطع الاتصال"),
        ["Control"] = ("Control (mouse + keyboard)", "تحكم (فأرة + لوحة مفاتيح)"),
        ["Quality"] = ("Quality", "الجودة"),
        ["Monitor"] = ("Monitor", "الشاشة"),
        ["PasteClipboard"] = ("Paste my clipboard", "لصق الحافظة"),
        ["RecordSession"] = ("Record session", "تسجيل الجلسة"),
        ["Send"] = ("Send", "إرسال"),
        ["Chat"] = ("Chat", "المحادثة"),
        ["Language"] = ("العربية", "English"),
    };

    public static string T(string key) =>
        Table.TryGetValue(key, out var v) ? (IsArabic ? v.Ar : v.En) : key;
}
