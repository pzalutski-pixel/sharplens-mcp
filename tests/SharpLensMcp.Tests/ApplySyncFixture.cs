using System.Text;

namespace SharpLensMcp.Tests;

// The shared on-disk solution for the sync and apply integrity tests: Lib (net8.0) and
// App (net8.0, references Lib). Every source file is UTF-8 without a BOM with LF line
// endings, so a test can assert exact bytes after a sync or an apply. Tests add the
// files their scenario needs before Restore / load.
public static class ApplySyncFixture
{
    public const string Widget =
        "namespace Lib;\n" +
        "\n" +
        "public class Widget\n" +
        "{\n" +
        "    public int Spin(int turns) => turns + 1;\n" +
        "}\n";

    public const string Service =
        "using Lib;\n" +
        "\n" +
        "namespace App;\n" +
        "\n" +
        "public class Service\n" +
        "{\n" +
        "    public int Run() => new Widget().Spin(1) + new Widget().Spin(2);\n" +
        "}\n";

    public const string Other =
        "namespace App;\n" +
        "\n" +
        "public class Other\n" +
        "{\n" +
        "}\n";

    // The windows-1252 file: "café" is the single byte 0xE9 for 'é', which is not valid
    // UTF-8, so a UTF-8 decode turns it into U+FFFD.
    public const string Latin =
        "namespace App;\n" +
        "\n" +
        "public class Latin\n" +
        "{\n" +
        "    public string Greet() => \"café\";\n" +
        "}\n";

    public static readonly Encoding Windows1252 = CodePagesEncoding(1252);

    // A BOM-less file that is not valid UTF-8 decodes with the system's fallback code page,
    // as the compiler reads it: windows-1252 on a Western Windows system, UTF-8 on Linux and
    // macOS (where the windows-1252 'é' becomes U+FFFD). True when the fallback is windows-1252.
    public static bool FallbackDecodesWindows1252 => CodePagesEncoding(0).CodePage == 1252;

    // Latin as this system decodes its windows-1252 bytes.
    public static string LatinAsDecoded => FallbackDecodesWindows1252 ? Latin : Latin.Replace('é', '�');

    // Creates Lib + App with the standard files. extraAppXml goes inside App.csproj's
    // <Project>. The caller writes any additional files, then calls Restore.
    public static DiskSolution Create(string extraAppXml = "")
    {
        var solution = new DiskSolution("Lib/Lib.csproj", "App/App.csproj");
        solution.WriteText("Lib/Lib.csproj", DiskSolution.SdkProject("net8.0"));
        solution.WriteText("App/App.csproj", DiskSolution.SdkProject("net8.0",
            new[] { "..\\Lib\\Lib.csproj" }, extraAppXml));
        solution.WriteText("Lib/Widget.cs", Widget);
        solution.WriteText("App/Service.cs", Service);
        solution.WriteText("App/Other.cs", Other);
        return solution;
    }

    private static Encoding CodePagesEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }
}
