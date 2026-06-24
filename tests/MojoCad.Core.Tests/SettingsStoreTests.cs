using System;
using System.IO;
using MojoCad.Core.Infrastructure;
using MojoCad.Core.Settings;
using Xunit;

namespace MojoCad.Core.Tests
{
    /// <summary>
    /// SettingsStore persists the whole MojoSettings document (everything except the API key). We round-
    /// trip it through a redirected %APPDATA% so we never touch the developer's real config, and assert
    /// that the enum fields (Discipline / LayerStandard, serialised as strings) and the ModelConfig
    /// survive intact - a silent serialisation regression here would lose the user's standards on restart.
    ///
    /// MojoPaths.Root is derived from Environment.SpecialFolder.ApplicationData, which reads %APPDATA% on
    /// Windows and XDG_CONFIG_HOME / $HOME/.config on Unix. We set all of them so the redirect works on
    /// whatever OS runs the tests, then anchor every assertion to the value MojoPaths.Root actually
    /// resolved to (rather than assuming a particular layout).
    /// </summary>
    [Collection("AppData environment")]
    public sealed class SettingsStoreTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string? _origAppData;
        private readonly string? _origXdg;
        private readonly string? _origHome;

        public SettingsStoreTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "mojocad-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            _origAppData = Environment.GetEnvironmentVariable("APPDATA");
            _origXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            _origHome = Environment.GetEnvironmentVariable("HOME");

            // Redirect the app-data resolver into the temp dir on every supported platform.
            Environment.SetEnvironmentVariable("APPDATA", _tempRoot);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempRoot);
            Environment.SetEnvironmentVariable("HOME", _tempRoot);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("APPDATA", _origAppData);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _origXdg);
            Environment.SetEnvironmentVariable("HOME", _origHome);
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }

        [Fact]
        public void Load_WhenNoFile_ReturnsDefaults()
        {
            var store = new SettingsStore();

            var loaded = store.Load();

            Assert.Equal(ModelCatalogDefaults.DefaultModel, loaded.Models.Model);
            Assert.Equal(DisciplinePreset.Architectural, loaded.Standards.Discipline);
            Assert.False(loaded.OnboardingCompleted);
        }

        [Fact]
        public void Save_WritesConfigUnderRedirectedAppData()
        {
            var store = new SettingsStore();

            store.Save(MojoSettings.CreateDefault());

            // The config file must land under the redirected root, never the developer's real %APPDATA%.
            Assert.True(File.Exists(MojoPaths.ConfigFile));
            Assert.StartsWith(_tempRoot, MojoPaths.Root, StringComparison.Ordinal);
        }

        [Fact]
        public void SaveLoad_RoundTrips_EnumsAndModelConfig()
        {
            var store = new SettingsStore();
            var original = new MojoSettings
            {
                Theme = "Light",
                OnboardingCompleted = true,
                RequirePlanApproval = true,
                Standards =
                {
                    Discipline = DisciplinePreset.FireAndLifeSafety,
                    LayerStandard = LayerStandard.Bs1192,
                    CodeReferences = "IBC 2021, NFPA 13",
                    AnnotationScale = "1:50",
                    CustomLayerStandard = "Office layer set v3"
                },
                Models =
                {
                    Model = "anthropic/claude-sonnet-4.6",
                    Temperature = 0.7,
                    MaxTokens = 8192,
                    ReasoningEffort = "high",
                    DenyDataCollection = false
                }
            };
            original.Models.FallbackModels.Clear();
            original.Models.FallbackModels.Add("openai/gpt-5.5");

            store.Save(original);
            var loaded = store.Load();

            // Enums must survive the string-enum serialisation.
            Assert.Equal(DisciplinePreset.FireAndLifeSafety, loaded.Standards.Discipline);
            Assert.Equal(LayerStandard.Bs1192, loaded.Standards.LayerStandard);

            // ModelConfig round-trips fully.
            Assert.Equal("anthropic/claude-sonnet-4.6", loaded.Models.Model);
            Assert.Equal(0.7, loaded.Models.Temperature, 10);
            Assert.Equal(8192, loaded.Models.MaxTokens);
            Assert.Equal("high", loaded.Models.ReasoningEffort);
            Assert.False(loaded.Models.DenyDataCollection);
            Assert.Equal(new[] { "openai/gpt-5.5" }, loaded.Models.FallbackModels.ToArray());

            // Remaining scalar/string fields.
            Assert.Equal("Light", loaded.Theme);
            Assert.True(loaded.OnboardingCompleted);
            Assert.True(loaded.RequirePlanApproval);
            Assert.Equal("IBC 2021, NFPA 13", loaded.Standards.CodeReferences);
            Assert.Equal("1:50", loaded.Standards.AnnotationScale);
            Assert.Equal("Office layer set v3", loaded.Standards.CustomLayerStandard);
        }

        [Fact]
        public void Save_IsAtomicAndOverwritesExisting()
        {
            var store = new SettingsStore();

            store.Save(new MojoSettings { Theme = "Dark" });
            store.Save(new MojoSettings { Theme = "Light" });

            Assert.Equal("Light", store.Load().Theme);
            // The atomic temp file must not linger after a successful save.
            Assert.False(File.Exists(MojoPaths.ConfigFile + ".tmp"));
        }
    }
}
