using TinyTracker.Presentation.Icons;
using Xunit;

namespace TinyTracker.Presentation.Tests.Icons;

public class IconSourceTests
{
    [Fact]
    public void MachineEntry_IsInHklm() =>
        Assert.Equal(new UninstallIcon(false, false, "{EXAMPLE-1234}"), IconSource.Of(@"ARP\Machine\X64\{EXAMPLE-1234}"));

    [Fact]
    public void X86MachineEntry_IsInThe32BitView() =>
        Assert.Equal(new UninstallIcon(false, true, "Example Editor"), IconSource.Of(@"ARP\Machine\X86\Example Editor"));

    [Fact]
    public void UserEntry_IsInHkcu() =>
        Assert.Equal(new UninstallIcon(true, false, "Example Chat"), IconSource.Of(@"ARP\User\X64\Example Chat"));

    [Fact]
    public void KeyPath_IsUnderUninstall() =>
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Example Editor", new UninstallIcon(false, false, "Example Editor").KeyPath);

    [Fact]
    public void MsixPackage_IsFoundByItsFullName() =>
        Assert.Equal(new PackagedIcon("Example.App_1.0.0.0_x64__abcdefgh"), IconSource.Of(@"MSIX\Example.App_1.0.0.0_x64__abcdefgh"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"ARP\Machine\X64\")]
    [InlineData(@"ARP\Somewhere\X64\Example")]
    [InlineData(@"MSIX\")]
    [InlineData(@"Example.Editor")]
    public void Unknown_HasNoSource(string? localId) => Assert.Null(IconSource.Of(localId));

    [Theory]
    [InlineData(@"C:\Program Files\Example\app.exe,0", @"C:\Program Files\Example\app.exe", 0)]
    [InlineData(@"C:\Program Files\Example\app.exe,-101", @"C:\Program Files\Example\app.exe", -101)]
    [InlineData(@"""C:\Program Files\Example\app.ico""", @"C:\Program Files\Example\app.ico", 0)]
    [InlineData(@"""C:\Program Files\Example\app.exe"",2", @"C:\Program Files\Example\app.exe", 2)]
    [InlineData(@"C:\Example, Inc\app.exe", @"C:\Example, Inc\app.exe", 0)]
    [InlineData(@"  %SystemRoot%\system32\app.dll , 3 ", @"%SystemRoot%\system32\app.dll", 3)]
    public void DisplayIcon_IsSplitIntoPathAndIndex(string value, string path, int index) => Assert.Equal((path, index), IconSource.DisplayIcon(value));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(@"""unclosed")]
    public void EmptyDisplayIcon_HasNoPath(string? value) => Assert.Null(IconSource.DisplayIcon(value));

    [Fact]
    public void MainProgram_IsTheOnlyOneLeft() =>
        Assert.Equal("editor.exe", IconSource.MainProgram(["unins000.exe", "editor.exe", "readme.txt", "UpdateHelper.exe"], "Example Editor"));

    [Fact]
    public void MainProgram_IsTheOneNamedAfterTheApp() =>
        Assert.Equal("ExampleEditor.exe", IconSource.MainProgram(["ExampleEditor.exe", "converter.exe"], "Example Editor 2.4"));

    [Fact]
    public void SeveralCandidates_MeanNoProgram() =>
        Assert.Null(IconSource.MainProgram(["one.exe", "two.exe"], "Example Editor"));

    [Fact]
    public void IconWithAlpha_IsPremultiplied()
    {
        byte[] pixels = [200, 100, 50, 128, 10, 20, 30, 0];
        IconSource.Premultiply(pixels, []);
        Assert.Equal([100, 50, 25, 128, 0, 0, 0, 0], pixels);
    }

    [Fact]
    public void IconWithoutAlpha_UsesItsMask()
    {
        byte[] pixels = [200, 100, 50, 0, 10, 20, 30, 0];
        byte[] mask = [0, 0, 0, 0, 255, 255, 255, 0];
        IconSource.Premultiply(pixels, mask);
        Assert.Equal([200, 100, 50, 255, 0, 0, 0, 0], pixels);
    }
}
