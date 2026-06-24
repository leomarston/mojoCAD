using System;
using System.Collections.Generic;

namespace MojoCad.Core.Settings
{
    /// <summary>Discipline presets tune the system prompt and the review gating (Fire &amp; Life-Safety is strictest).</summary>
    public enum DisciplinePreset
    {
        Architectural,
        FireAndLifeSafety,
        Mep,
        Structural,
        General
    }

    /// <summary>Layer-naming standard the agent is told to follow.</summary>
    public enum LayerStandard
    {
        AiaNcs,      // US National CAD Standard (A-WALL, M-DUCT, FP-SPKL, ...)
        Bs1192,      // UK
        Iso13567,
        Custom
    }

    /// <summary>
    /// User-visible, persisted standards that template the system prompt. The user sees and can override
    /// every field, so the AI's behaviour is transparent and project-specific.
    /// </summary>
    public sealed class StandardsProfile
    {
        public DisciplinePreset Discipline { get; set; } = DisciplinePreset.Architectural;

        public LayerStandard LayerStandard { get; set; } = LayerStandard.AiaNcs;

        /// <summary>For <see cref="LayerStandard.Custom"/>: a free-text description of the layer convention.</summary>
        public string? CustomLayerStandard { get; set; }

        /// <summary>Free-text code references injected verbatim, e.g. "IBC 2021, NFPA 13, NFPA 101".</summary>
        public string CodeReferences { get; set; } = string.Empty;

        /// <summary>Plotting/annotation scale the agent should size text/dims for, e.g. "1:50" or "1/4\"=1'".</summary>
        public string? AnnotationScale { get; set; }

        /// <summary>
        /// Optional extra house-rules appended to the system prompt (office standards, preferred blocks, etc.).
        /// </summary>
        public string AdditionalGuidance { get; set; } = string.Empty;
    }

    /// <summary>Model routing and sampling configuration.</summary>
    public sealed class ModelConfig
    {
        /// <summary>Primary model id, e.g. "anthropic/claude-opus-4.8".</summary>
        public string Model { get; set; } = ModelCatalogDefaults.DefaultModel;

        /// <summary>Ordered fallback models for OpenRouter's auto-failover (the "models" array).</summary>
        public List<string> FallbackModels { get; set; } = new List<string>(ModelCatalogDefaults.DefaultFallbacks);

        public double Temperature { get; set; } = 0.2;

        public int MaxTokens { get; set; } = 4096;

        /// <summary>Optional reasoning-effort hint passed through to capable models.</summary>
        public string? ReasoningEffort { get; set; }

        /// <summary>Deny providers that log/train on prompts - protects proprietary CAD data.</summary>
        public bool DenyDataCollection { get; set; } = true;
    }

    /// <summary>Known-good defaults. The live model list is fetched from OpenRouter's /models at runtime.</summary>
    public static class ModelCatalogDefaults
    {
        public const string DefaultModel = "anthropic/claude-opus-4.8";

        public static readonly string[] DefaultFallbacks =
        {
            "anthropic/claude-sonnet-4.6",
            "openai/gpt-5.5"
        };

        /// <summary>Curated alternatives surfaced first in the model picker before the full /models list loads.</summary>
        public static readonly (string Id, string Label, string Note)[] Recommended =
        {
            ("anthropic/claude-opus-4.8", "Claude Opus 4.8", "Strongest tool-use & instruction-following. Default for life-safety work."),
            ("anthropic/claude-sonnet-4.6", "Claude Sonnet 4.6", "Near-Opus quality at lower cost. Good default for everyday drafting."),
            ("openai/gpt-5.5", "GPT-5.5", "Rock-solid strict tool calling. Primary cross-vendor fallback."),
            ("google/gemini-3.1-pro-preview", "Gemini 3.1 Pro", "Largest context, cheapest flagship. Use with require_parameters.")
        };
    }

    /// <summary>Root, persisted settings document (stored as %APPDATA%\mojoCAD\config.json; the key lives in DPAPI, not here).</summary>
    public sealed class MojoSettings
    {
        public ModelConfig Models { get; set; } = new ModelConfig();

        public StandardsProfile Standards { get; set; } = new StandardsProfile();

        /// <summary>UI theme: "Dark" or "Light".</summary>
        public string Theme { get; set; } = "Dark";

        /// <summary>Whether the first-run onboarding has been completed.</summary>
        public bool OnboardingCompleted { get; set; }

        /// <summary>If true, the plan-before-act gate is enforced for every turn (always on for Fire &amp; Life-Safety).</summary>
        public bool RequirePlanApproval { get; set; }

        /// <summary>
        /// Enable the experimental <c>run_command</c> tool, which lets the agent reach AutoCAD's full command
        /// set (FILLET, TRIM, XREF, ...) as staged, reviewed operations. OFF by default: command output cannot
        /// be shape-previewed and the feature should be validated against your AutoCAD version before relying
        /// on it. Even when on, every command is reviewed and applied as one undo step - never auto-run.
        /// </summary>
        public bool EnableCommandExecution { get; set; }

        public static MojoSettings CreateDefault() => new MojoSettings();
    }
}
