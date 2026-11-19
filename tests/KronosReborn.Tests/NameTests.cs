using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// The app is called Spellweaver. It was Kronos Reborn, a name that stays only where changing it would
/// break installed copies: the GitHub repository, the data folder, the code, and the text update
/// signatures cover.
/// </summary>
public class NameTests
{
    private const string Name = "Spellweaver";

    private static string Read(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KronosReborn.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(segments)));
    }

    [Theory]
    [InlineData("src", "KronosReborn", "MainWindow.axaml")]
    [InlineData("src", "KronosReborn", "MainWindow.axaml.cs")]
    [InlineData("src", "KronosReborn", "App.axaml.cs")]
    [InlineData("src", "KronosReborn", "KronosReborn.csproj")]
    [InlineData("src", "KronosReborn.Core", "Data", "Updates", "UpdateChecker.cs")]
    [InlineData("src", "KronosReborn.Core", "Data", "Diagnostics", "Diagnostics.cs")]
    [InlineData("src", "KronosReborn.Core", "UI", "DiagnosticsViewModel.cs")]
    [InlineData("package", "config.cmd")]
    public void TheOldNameIsNotShownToUsers(params string[] file)
    {
        Assert.DoesNotContain("Kronos Reborn", Read(file), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWindowAndInstallerUseTheNewName()
    {
        Assert.Contains($"Title=\"{Name}\"", Read("src", "KronosReborn", "MainWindow.axaml"), StringComparison.Ordinal);
        Assert.Matches($"#define AppName \"{Name}\"", Read("package", "Installer.iss"));
        Assert.Matches($"OutputBaseFilename={Name}-build-", Read("package", "Installer.iss"));
        Assert.Matches($@"set output_installer=.*\\{Name}-build-%app_version%-installer\.exe", Read("package", "config.cmd"));
        Assert.Matches($@"set output_zip=.*\\{Name}-build-%app_version%-portable\.zip", Read("package", "config.cmd"));
        // Shown by Task Manager and Windows' "Open with" for the exe, which keeps its old file name.
        Assert.Contains($"<AssemblyTitle>{Name}</AssemblyTitle>", Read("src", "KronosReborn", "KronosReborn.csproj"), StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasesAreTitledWithTheNewName()
    {
        Assert.Contains("--title \"Spellweaver build $(", Read(".github", "workflows", "release.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpgradeRemovesTheShortcutsNamedAfterTheOldName()
    {
        // Inno keeps an upgraded copy's Start menu folder and desktop-icon choice, so without these an
        // upgrade would leave "Kronos Reborn" shortcuts beside the new "Spellweaver" ones.
        var iss = Read("package", "Installer.iss");
        var section = Regex.Match(iss, @"\[InstallDelete\](?<body>[\s\S]*?)(\n\[|$)").Groups["body"].Value;

        Assert.Contains(@"{autodesktop}\Kronos Reborn.lnk", section, StringComparison.Ordinal);
        Assert.Contains(@"{autoprograms}\Kronos Reborn\Kronos Reborn.lnk", section, StringComparison.Ordinal);
        Assert.Contains(@"{autoprograms}\Kronos Reborn\Uninstall Kronos Reborn.lnk", section, StringComparison.Ordinal);
        Assert.Contains(@"Type: dirifempty; Name: ""{autoprograms}\Kronos Reborn""", section, StringComparison.Ordinal);

        // Otherwise Inno keeps using the old "Kronos Reborn" Start menu folder for the new shortcuts.
        Assert.Contains("UsePreviousGroup=no", iss, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpdateOfACopyInstalledForEveryoneStaysInstalledForEveryone()
    {
        // Build 7's updater passes /CURRENTUSER for a copy in Program Files (x86). Without this the
        // installer would add a second, per-user "Spellweaver" beside the machine-wide "Kronos Reborn".
        var code = Read("package", "Installer.iss");
        Assert.Contains("function InitializeSetup", code, StringComparison.Ordinal);
        Assert.Contains("{6C3D9E24-1B7A-4E58-9F0C-2D5A83B71E40}_is1", code, StringComparison.Ordinal);
        Assert.Contains("/ALLUSERS", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNamesInstalledCopiesDependOnAreUnchanged()
    {
        // The installer's AppId makes an update replace the old install rather than add a second one.
        Assert.Contains("6C3D9E24-1B7A-4E58-9F0C-2D5A83B71E40", Read("package", "Installer.iss"), StringComparison.Ordinal);
        // Installed copies look for updates here and accept only links into it.
        Assert.Contains("repos/SpaceJamp/Kronos-Reborn/releases/latest", Read("src", "KronosReborn.Core", "Data", "Updates", "UpdateChecker.cs"), StringComparison.Ordinal);
        Assert.Equal("KronosReborn", KronosReborn.Data.Persistence.DataPaths.AppFolderName);
        // Renaming the exe would leave the old one behind on an upgrade.
        Assert.Contains("#define AppExeName \"KronosReborn.exe\"", Read("package", "Installer.iss"), StringComparison.Ordinal);
        // Every installed copy checks update signatures over exactly this text.
        Assert.Equal("KronosReborn|8|abc", System.Text.Encoding.UTF8.GetString(KronosReborn.Data.Updates.UpdateChecker.SignedMessage("8", "abc")));
    }
}
