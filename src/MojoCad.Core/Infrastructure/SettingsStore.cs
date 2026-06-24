using System;
using System.IO;
using System.Text.Json;
using MojoCad.Core.Settings;

namespace MojoCad.Core.Infrastructure
{
    /// <summary>
    /// Loads/saves <see cref="MojoSettings"/> as %APPDATA%\mojoCAD\config.json. The API key is NOT part
    /// of this document - it lives in the DPAPI store. Writes are atomic (temp + move).
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public MojoSettings Load()
        {
            try
            {
                if (!File.Exists(MojoPaths.ConfigFile))
                    return MojoSettings.CreateDefault();

                string json = File.ReadAllText(MojoPaths.ConfigFile);
                var settings = JsonSerializer.Deserialize<MojoSettings>(json, Options);
                return settings ?? MojoSettings.CreateDefault();
            }
            catch
            {
                // A corrupt config should never brick the plugin; fall back to defaults.
                return MojoSettings.CreateDefault();
            }
        }

        public void Save(MojoSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            MojoPaths.EnsureCreated();
            string json = JsonSerializer.Serialize(settings, Options);
            string tmp = MojoPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(MojoPaths.ConfigFile))
                File.Delete(MojoPaths.ConfigFile);
            File.Move(tmp, MojoPaths.ConfigFile);
        }
    }
}
