using System;
using System.Collections.Generic;
using System.Text.Json;
using MojoCad.Core.Geometry;

namespace MojoCad.Agent.Tools
{
    /// <summary>An argument-validation failure carrying the uniform error fields fed back to the model.</summary>
    public sealed class ToolArgException : Exception
    {
        public string Code { get; }
        public string? Offending { get; }
        public string? Hint { get; }

        public ToolArgException(string code, string message, string? offending = null, string? hint = null)
            : base(message)
        {
            Code = code;
            Offending = offending;
            Hint = hint;
        }
    }

    /// <summary>
    /// Safe, typed reader over a tool call's JSON arguments. Every accessor throws a
    /// <see cref="ToolArgException"/> with a specific code/hint so the model can self-correct rather
    /// than the plugin coercing bad input silently.
    /// </summary>
    public sealed class ToolArgs
    {
        private readonly JsonElement _root;

        public ToolArgs(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) argumentsJson = "{}";
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                _root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new ToolArgException("INVALID_JSON", "The tool arguments were not valid JSON: " + ex.Message);
            }
        }

        public bool Has(string name) => _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out _);

        public string GetString(string name)
        {
            var e = Require(name);
            if (e.ValueKind != JsonValueKind.String)
                throw Bad(name, "expected a string");
            return e.GetString() ?? string.Empty;
        }

        public string? GetStringOrNull(string name)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            if (e.ValueKind != JsonValueKind.String) throw Bad(name, "expected a string");
            return e.GetString();
        }

        public double GetDouble(string name)
        {
            var e = Require(name);
            if (e.ValueKind != JsonValueKind.Number) throw Bad(name, "expected a number");
            return e.GetDouble();
        }

        public double? GetDoubleOrNull(string name)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            if (e.ValueKind != JsonValueKind.Number) throw Bad(name, "expected a number");
            return e.GetDouble();
        }

        public int GetInt(string name, int? fallback = null)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
            {
                if (fallback.HasValue) return fallback.Value;
                throw Missing(name);
            }
            if (e.ValueKind != JsonValueKind.Number) throw Bad(name, "expected an integer");
            return e.GetInt32();
        }

        public bool GetBool(string name, bool fallback = false)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return fallback;
            return e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.False ? false : fallback);
        }

        public string GetEnum(string name, string[] allowed, string? fallback = null)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
            {
                if (fallback != null) return fallback;
                throw Missing(name);
            }
            string val = e.GetString() ?? string.Empty;
            foreach (var a in allowed) if (string.Equals(a, val, StringComparison.OrdinalIgnoreCase)) return a;
            throw new ToolArgException("BAD_ENUM", $"'{name}' must be one of: {string.Join(", ", allowed)}.", val,
                $"Use one of the allowed values for '{name}'.");
        }

        public Pt GetPoint(string name)
        {
            var e = Require(name);
            return ReadPoint(name, e);
        }

        public Pt? GetPointOrNull(string name)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            return ReadPoint(name, e);
        }

        public List<Pt> GetPoints(string name, int minCount = 1)
        {
            var e = Require(name);
            if (e.ValueKind != JsonValueKind.Array) throw Bad(name, "expected an array of points");
            var list = new List<Pt>();
            foreach (var item in e.EnumerateArray())
                list.Add(ReadPoint(name, item));
            if (list.Count < minCount)
                throw new ToolArgException("NOT_ENOUGH_POINTS", $"'{name}' needs at least {minCount} point(s).", null,
                    $"Provide at least {minCount} coordinate pair(s).");
            return list;
        }

        public List<Pt>? GetPointsOrNull(string name)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            return GetPoints(name);
        }

        public List<double>? GetDoubleArrayOrNull(string name)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            if (e.ValueKind != JsonValueKind.Array) throw Bad(name, "expected an array of numbers");
            var list = new List<double>();
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number) throw Bad(name, "expected an array of numbers");
                list.Add(item.GetDouble());
            }
            return list;
        }

        public List<string> GetStringList(string name, int minCount = 1)
        {
            var e = Require(name);
            if (e.ValueKind != JsonValueKind.Array) throw Bad(name, "expected an array of strings");
            var list = new List<string>();
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw Bad(name, "expected an array of strings");
                list.Add(item.GetString() ?? string.Empty);
            }
            if (list.Count < minCount)
                throw new ToolArgException("EMPTY_LIST", $"'{name}' must contain at least {minCount} item(s).");
            return list;
        }

        public List<string>? GetStringListOrNull(string name)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            return GetStringList(name, 0);
        }

        public Dictionary<string, string>? GetStringMapOrNull(string name)
        {
            if (!_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            if (e.ValueKind != JsonValueKind.Object) throw Bad(name, "expected an object of string values");
            var map = new Dictionary<string, string>();
            foreach (var p in e.EnumerateObject())
                map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "") : p.Value.ToString();
            return map;
        }

        /// <summary>Read the optional nested "props" override object (colour/linetype/lineweight).</summary>
        public MojoCad.Core.Changes.PropertyOverrides? GetPropsOrNull()
        {
            if (!_root.TryGetProperty("props", out var e) || e.ValueKind != JsonValueKind.Object) return null;
            var p = new MojoCad.Core.Changes.PropertyOverrides();
            bool any = false;
            if (e.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.Number) { p.ColorIndex = c.GetInt32(); any = true; }
            if (e.TryGetProperty("true_color", out var tc) && tc.ValueKind == JsonValueKind.Number) { p.TrueColor = tc.GetInt32(); any = true; }
            if (e.TryGetProperty("linetype", out var lt) && lt.ValueKind == JsonValueKind.String) { p.Linetype = lt.GetString(); any = true; }
            if (e.TryGetProperty("lineweight", out var lw) && lw.ValueKind == JsonValueKind.Number) { p.LineweightHundredthsMm = lw.GetInt32(); any = true; }
            return any ? p : null;
        }

        public Vec? GetVectorOrNull(string name)
        {
            var arr = GetDoubleArrayOrNull(name);
            if (arr == null) return null;
            if (arr.Count < 2) throw Bad(name, "expected [dx, dy] (or [dx, dy, dz])");
            return new Vec(arr[0], arr[1], arr.Count > 2 ? arr[2] : 0.0);
        }

        private Pt ReadPoint(string name, JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Array) throw Bad(name, "expected a coordinate array [x, y]");
            var nums = new List<double>();
            foreach (var n in e.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Number) throw Bad(name, "coordinates must be numbers");
                nums.Add(n.GetDouble());
            }
            if (nums.Count < 2) throw Bad(name, "a coordinate needs at least [x, y]");
            return new Pt(nums[0], nums[1], nums.Count > 2 ? nums[2] : 0.0);
        }

        private JsonElement Require(string name)
        {
            if (_root.ValueKind != JsonValueKind.Object || !_root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
                throw Missing(name);
            return e;
        }

        private static ToolArgException Missing(string name) =>
            new ToolArgException("MISSING_ARG", $"Required argument '{name}' is missing.", name, $"Add '{name}' to the call.");

        private static ToolArgException Bad(string name, string why) =>
            new ToolArgException("BAD_ARG", $"Argument '{name}' is invalid: {why}.", name, $"Fix '{name}' and retry.");
    }
}
