using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InnerVoice.App;

/// Whether a word is in the dictionary, for Find Terms (the Mac passes its system spell
/// checker). On Windows: the Windows Spell Checking API (ISpellChecker), English first.
/// Elsewhere (development on a Mac) nothing is "known", so Find Terms lists more.
public static class SpellCheck
{
    static readonly Dictionary<string, bool> Cache = new(StringComparer.Ordinal);
    static object? checker;
    static bool tried;

    public static bool IsKnown(string word)
    {
        if (word.Length == 0) return true;
        lock (Cache)
        {
            if (Cache.TryGetValue(word, out var known)) return known;
            known = OperatingSystem.IsWindows() && CheckWindows(word);
            Cache[word] = known;
            return known;
        }
    }

    [SupportedOSPlatform("windows")]
    static bool CheckWindows(string word)
    {
        try
        {
            if (!tried)
            {
                tried = true;
                var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
                foreach (var lang in new[] { System.Globalization.CultureInfo.CurrentUICulture.Name, "en-US", "en-GB" })
                {
                    if (factory.IsSupported(lang) != 0) { checker = factory.CreateSpellChecker(lang); break; }
                }
            }
            if (checker is not ISpellChecker sc) return false;
            var errors = sc.Check(word);
            int hr = errors.Next(out var error);
            if (error is not null) Marshal.ReleaseComObject(error);
            Marshal.ReleaseComObject(errors);
            return hr != 0; // S_FALSE: no errors, the word is known
        }
        catch (COMException) { return false; }
    }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    class SpellCheckerFactoryClass { }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        [return: MarshalAs(UnmanagedType.Interface)] object get_SupportedLanguages();
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        [return: MarshalAs(UnmanagedType.Interface)] ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string get_LanguageTag();
        [return: MarshalAs(UnmanagedType.Interface)] IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.Interface)] out object? value);
    }
}
