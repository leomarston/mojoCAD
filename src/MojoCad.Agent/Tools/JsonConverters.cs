using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using MojoCad.Core.Geometry;

namespace MojoCad.Agent.Tools
{
    /// <summary>Serialise <see cref="Pt"/> as a compact [x, y, z] array for tool results.</summary>
    public sealed class PtJsonConverter : JsonConverter<Pt>
    {
        public override Pt Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            var nums = new List<double>();
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    if (reader.TokenType == JsonTokenType.Number) nums.Add(reader.GetDouble());
            }
            return nums.Count >= 2 ? new Pt(nums[0], nums[1], nums.Count > 2 ? nums[2] : 0) : default;
        }

        public override void Write(Utf8JsonWriter writer, Pt value, JsonSerializerOptions o)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Round(value.X));
            writer.WriteNumberValue(Round(value.Y));
            if (Math.Abs(value.Z) > 1e-9) writer.WriteNumberValue(Round(value.Z));
            writer.WriteEndArray();
        }

        private static double Round(double v) => Math.Round(v, 6);
    }

    public sealed class VecJsonConverter : JsonConverter<Vec>
    {
        public override Vec Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => default;

        public override void Write(Utf8JsonWriter writer, Vec value, JsonSerializerOptions o)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Math.Round(value.X, 6));
            writer.WriteNumberValue(Math.Round(value.Y, 6));
            if (Math.Abs(value.Z) > 1e-9) writer.WriteNumberValue(Math.Round(value.Z, 6));
            writer.WriteEndArray();
        }
    }

    /// <summary>Serialise <see cref="Bounds"/> as { "min": [x,y], "max": [x,y] }.</summary>
    public sealed class BoundsJsonConverter : JsonConverter<Bounds>
    {
        public override Bounds Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => default;

        public override void Write(Utf8JsonWriter writer, Bounds value, JsonSerializerOptions o)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("min");
            JsonSerializer.Serialize(writer, value.Min, o);
            writer.WritePropertyName("max");
            JsonSerializer.Serialize(writer, value.Max, o);
            writer.WriteEndObject();
        }
    }

    public static class ToolJson
    {
        public static readonly JsonSerializerOptions ReadResultOptions = Build();

        private static JsonSerializerOptions Build()
        {
            var o = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            o.Converters.Add(new PtJsonConverter());
            o.Converters.Add(new VecJsonConverter());
            o.Converters.Add(new BoundsJsonConverter());
            return o;
        }
    }
}
