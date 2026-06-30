using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MojoCad.Core.Agent;
using MojoCad.Core.Composition;
using MojoCad.Core.Settings;

namespace MojoCad.Ui.ViewModels
{
    /// <summary>
    /// The settings screen's view-model: the OpenRouter key (validated and stored via DPAPI), the model
    /// picker (seeded from the curated catalogue, refreshable from /models), and the standards profile that
    /// templates the system prompt. Everything persists through <see cref="MojoServices.Settings"/> and the
    /// secure store; nothing here ever writes the key to the config file.
    /// </summary>
    public sealed partial class SettingsViewModel : ObservableObject
    {
        private readonly MojoServices _services;
        private readonly Action _onClosed;
        private MojoSettings _settings;

        public SettingsViewModel(MojoServices services, Action onClosed)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _onClosed = onClosed ?? (() => { });
            _settings = _services.Settings.Load();

            // Seed the model list from the curated recommendations so the picker is useful before any
            // network call. A "Refresh models" pulls the live, tool-capable list from OpenRouter.
            foreach (var (id, label, note) in ModelCatalogDefaults.Recommended)
                Models.Add(new ModelOption(id, label, note));
            EnsureModelPresent(_settings.Models.Model);

            // Hydrate the fields from persisted settings.
            _selectedModelId = _settings.Models.Model;
            _temperature = _settings.Models.Temperature;
            _maxTokens = _settings.Models.MaxTokens;
            _fallbackModels = string.Join(", ", _settings.Models.FallbackModels);
            _denyDataCollection = _settings.Models.DenyDataCollection;

            _discipline = _settings.Standards.Discipline;
            _layerStandard = _settings.Standards.LayerStandard;
            _customLayerStandard = _settings.Standards.CustomLayerStandard ?? string.Empty;
            _codeReferences = _settings.Standards.CodeReferences;
            _annotationScale = _settings.Standards.AnnotationScale ?? string.Empty;
            _additionalGuidance = _settings.Standards.AdditionalGuidance;
            _requirePlanApproval = _settings.RequirePlanApproval;
            _enableCommandExecution = _settings.EnableCommandExecution;

            HasKey = _services.SecureStore.HasApiKey;
            KeyStatus = HasKey ? "A key is stored. Test it or paste a new one." : "No key stored yet.";
            KeyStatusState = HasKey ? StatusKind.Neutral : StatusKind.Warning;
        }

        // ----- API key --------------------------------------------------------------------------

        /// <summary>Bound to the PasswordBox via the attached behavior. Starts empty even when a key exists
        /// (we never round-trip the secret into a control we don't have to). Notifies the Save/Test commands
        /// so their buttons enable the moment a key is typed or pasted.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
        [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshModelsCommand))]
        private string _apiKey = string.Empty;

        /// <summary>When true the key is shown in a normal TextBox instead of a PasswordBox.</summary>
        [ObservableProperty]
        private bool _isKeyVisible;

        [ObservableProperty]
        private bool _hasKey;

        [ObservableProperty]
        private string _keyStatus = string.Empty;

        [ObservableProperty]
        private StatusKind _keyStatusState = StatusKind.Neutral;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
        private bool _isTesting;

        [RelayCommand]
        private void ToggleKeyVisibility() => IsKeyVisible = !IsKeyVisible;

        private bool CanTest() => !IsTesting && !string.IsNullOrWhiteSpace(ApiKey);

        [RelayCommand(CanExecute = nameof(CanTest))]
        private async Task TestConnection()
        {
            IsTesting = true;
            KeyStatus = "Testing…";
            KeyStatusState = StatusKind.Neutral;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var info = await _services.Account.ValidateKeyAsync(ApiKey.Trim(), cts.Token);
                if (info.IsValid)
                {
                    KeyStatus = DescribeKey(info);
                    KeyStatusState = StatusKind.Ok;
                }
                else
                {
                    KeyStatus = "Key rejected by OpenRouter.";
                    KeyStatusState = StatusKind.Error;
                }
            }
            catch (OpenRouterAccountException ex)
            {
                KeyStatus = ex.Reason switch
                {
                    OpenRouterAccountError.InvalidKey => "Invalid key - check for a copy/paste slip.",
                    OpenRouterAccountError.Network => "Network error reaching OpenRouter.",
                    OpenRouterAccountError.RateLimited => "Rate limited - try again in a moment.",
                    _ => ex.Message
                };
                KeyStatusState = StatusKind.Error;
            }
            catch (Exception ex)
            {
                KeyStatus = ex.Message;
                KeyStatusState = StatusKind.Error;
            }
            finally
            {
                IsTesting = false;
            }
        }

        private bool CanSaveKey() => !string.IsNullOrWhiteSpace(ApiKey);

        [RelayCommand(CanExecute = nameof(CanSaveKey))]
        private void SaveKey()
        {
            // DPAPI-encrypted at rest, CurrentUser scope - never the config file, never the repo.
            _services.SecureStore.SaveApiKey(ApiKey.Trim());
            HasKey = true;
            KeyStatus = "Key saved (encrypted on this machine).";
            KeyStatusState = StatusKind.Ok;
        }

        [RelayCommand]
        private void DeleteKey()
        {
            _services.SecureStore.DeleteApiKey();
            HasKey = false;
            ApiKey = string.Empty;
            KeyStatus = "Key removed.";
            KeyStatusState = StatusKind.Warning;
        }

        private static string DescribeKey(KeyInfo info)
        {
            var bits = new List<string> { "Key valid" };
            if (!string.IsNullOrWhiteSpace(info.Label)) bits.Add(info.Label!);
            if (info.LimitRemaining is double rem)
                bits.Add($"${rem:0.00} remaining");
            else if (info.Limit is double lim)
                bits.Add($"${lim:0.00} limit");
            else if (info.IsFreeTier)
                bits.Add("free tier");
            if (info.Usage is double used)
                bits.Add($"${used:0.00} used");
            return string.Join(" · ", bits);
        }

        // ----- model picker ---------------------------------------------------------------------

        public ObservableCollection<ModelOption> Models { get; } = new();

        [ObservableProperty]
        private string _selectedModelId = string.Empty;

        [ObservableProperty]
        private string _fallbackModels = string.Empty;

        [ObservableProperty]
        private double _temperature;

        [ObservableProperty]
        private int _maxTokens;

        [ObservableProperty]
        private bool _denyDataCollection;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshModelsCommand))]
        private bool _isRefreshingModels;

        [ObservableProperty]
        private string? _modelStatus;

        // Allow refreshing as soon as a key is pasted (even before it's saved) - RefreshModels falls back
        // to the typed key. Otherwise the button stays dead until Save, which is confusing.
        private bool CanRefreshModels() =>
            !IsRefreshingModels && (!string.IsNullOrWhiteSpace(ApiKey) || _services.SecureStore.HasApiKey);

        [RelayCommand(CanExecute = nameof(CanRefreshModels))]
        private async Task RefreshModels()
        {
            var key = !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : _services.SecureStore.LoadApiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                ModelStatus = "Add a key first to list models.";
                return;
            }

            IsRefreshingModels = true;
            ModelStatus = "Loading models…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var live = await _services.Account.ListToolCapableModelsAsync(key!, cts.Token);
                MergeLiveModels(live);
                ModelStatus = $"{live.Count} tool-capable models available.";
            }
            catch (OpenRouterAccountException ex)
            {
                ModelStatus = ex.Reason == OpenRouterAccountError.InvalidKey
                    ? "Invalid key - can't list models."
                    : "Couldn't load models. Showing the curated list.";
            }
            catch (Exception)
            {
                ModelStatus = "Couldn't load models. Showing the curated list.";
            }
            finally
            {
                IsRefreshingModels = false;
            }
        }

        private void MergeLiveModels(IReadOnlyList<ModelInfo> live)
        {
            // Keep the curated recommendations on top (they carry helpful notes), then append the rest.
            var known = new HashSet<string>(Models.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var m in live.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!m.SupportsTools) continue; // belt & braces; the port already filters
                if (known.Contains(m.Id)) continue;
                var note = m.ContextLength is int ctx ? $"{ctx / 1000}K context" : string.Empty;
                Models.Add(new ModelOption(m.Id, string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name, note));
                known.Add(m.Id);
            }
            EnsureModelPresent(SelectedModelId);
        }

        private void EnsureModelPresent(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (!Models.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                Models.Insert(0, new ModelOption(id, id, "Current selection"));
        }

        // ----- standards ------------------------------------------------------------------------

        public IReadOnlyList<DisciplinePreset> Disciplines { get; } =
            Enum.GetValues(typeof(DisciplinePreset)).Cast<DisciplinePreset>().ToList();

        public IReadOnlyList<LayerStandard> LayerStandards { get; } =
            Enum.GetValues(typeof(LayerStandard)).Cast<LayerStandard>().ToList();

        [ObservableProperty]
        private DisciplinePreset _discipline;

        [ObservableProperty]
        private LayerStandard _layerStandard;

        [ObservableProperty]
        private string _customLayerStandard = string.Empty;

        [ObservableProperty]
        private string _codeReferences = string.Empty;

        [ObservableProperty]
        private string _annotationScale = string.Empty;

        [ObservableProperty]
        private string _additionalGuidance = string.Empty;

        [ObservableProperty]
        private bool _requirePlanApproval;

        /// <summary>Opt-in: let the agent reach AutoCAD's full command set via the experimental run_command tool.</summary>
        [ObservableProperty]
        private bool _enableCommandExecution;

        public bool IsCustomLayerStandard => LayerStandard == LayerStandard.Custom;
        partial void OnLayerStandardChanged(LayerStandard value) => OnPropertyChanged(nameof(IsCustomLayerStandard));

        /// <summary>Fire &amp; Life-Safety always forces the plan-approval gate, so reflect that in the UI.</summary>
        public bool IsPlanApprovalForced => Discipline == DisciplinePreset.FireAndLifeSafety;
        partial void OnDisciplineChanged(DisciplinePreset value)
        {
            OnPropertyChanged(nameof(IsPlanApprovalForced));
            if (value == DisciplinePreset.FireAndLifeSafety)
                RequirePlanApproval = true;
        }

        // ----- save / close ---------------------------------------------------------------------

        [ObservableProperty]
        private string? _savedNotice;

        [RelayCommand]
        private void Save()
        {
            // If the user typed a fresh key but didn't hit "Save key" explicitly, persist it on Save too -
            // it's the obvious intent and avoids a silently-lost key.
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                _services.SecureStore.SaveApiKey(ApiKey.Trim());
                HasKey = true;
            }

            _settings.Models.Model = SelectedModelId;
            _settings.Models.Temperature = Clamp(Temperature, 0.0, 2.0);
            _settings.Models.MaxTokens = Math.Max(256, MaxTokens);
            _settings.Models.FallbackModels = ParseFallbacks(FallbackModels);
            _settings.Models.DenyDataCollection = DenyDataCollection;

            _settings.Standards.Discipline = Discipline;
            _settings.Standards.LayerStandard = LayerStandard;
            _settings.Standards.CustomLayerStandard = string.IsNullOrWhiteSpace(CustomLayerStandard) ? null : CustomLayerStandard.Trim();
            _settings.Standards.CodeReferences = CodeReferences?.Trim() ?? string.Empty;
            _settings.Standards.AnnotationScale = string.IsNullOrWhiteSpace(AnnotationScale) ? null : AnnotationScale.Trim();
            _settings.Standards.AdditionalGuidance = AdditionalGuidance?.Trim() ?? string.Empty;

            _settings.RequirePlanApproval = RequirePlanApproval || IsPlanApprovalForced;
            _settings.EnableCommandExecution = EnableCommandExecution;
            _settings.OnboardingCompleted = _services.SecureStore.HasApiKey;

            _services.Settings.Save(_settings);

            SavedNotice = "Settings saved.";
        }

        [RelayCommand]
        private void SaveAndClose()
        {
            Save();
            _onClosed();
        }

        [RelayCommand]
        private void Close() => _onClosed();

        private static List<string> ParseFallbacks(string raw) =>
            (raw ?? string.Empty)
                .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));

        /// <summary>One model entry in the picker: stable id plus a friendly label and short note.</summary>
        public sealed record ModelOption(string Id, string Label, string Note);

        public enum StatusKind { Neutral, Ok, Warning, Error }
    }
}
