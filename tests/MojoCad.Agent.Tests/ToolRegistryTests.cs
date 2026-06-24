using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MojoCad.Agent.Tools;
using Xunit;

namespace MojoCad.Agent.Tests
{
    /// <summary>
    /// ToolRegistry.BuildAll is the entire tool surface sent to the model on every request. If a tool
    /// goes missing the agent silently loses a capability; if a schema is malformed the router rejects the
    /// whole request. So we assert (a) every ToolNames constant is present exactly once and nothing extra
    /// leaks in, and (b) every tool's parameters is a well-formed JSON-Schema object.
    /// </summary>
    public sealed class ToolRegistryTests
    {
        private static IEnumerable<string> AllToolNameConstants() =>
            typeof(ToolNames)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!);

        [Fact]
        public void BuildAll_ContainsEveryToolName_ExactlyOnce()
        {
            // Include the opt-in run_command power tool so the FULL surface is checked against ToolNames.
            var built = ToolRegistry.BuildAll(includeCommandTool: true).Select(t => t.Function.Name).ToList();
            var expected = AllToolNameConstants().ToHashSet();

            // Every declared tool name is offered...
            foreach (var name in expected)
                Assert.Contains(name, built);

            // ...and nothing extra/duplicated is offered.
            Assert.Equal(expected.Count, built.Count);
            Assert.Equal(built.Count, built.Distinct().Count());
        }

        [Fact]
        public void BuildAll_EveryTool_HasObjectParametersSchema()
        {
            foreach (var tool in ToolRegistry.BuildAll(includeCommandTool: true))
            {
                Assert.Equal("function", tool.Type);
                Assert.False(string.IsNullOrWhiteSpace(tool.Function.Description),
                    $"{tool.Function.Name} should carry a description.");

                JsonElement schema = tool.Function.Parameters;
                Assert.Equal(JsonValueKind.Object, schema.ValueKind);

                Assert.True(schema.TryGetProperty("type", out var type), $"{tool.Function.Name} schema lacks 'type'.");
                Assert.Equal("object", type.GetString());

                Assert.True(schema.TryGetProperty("properties", out var props), $"{tool.Function.Name} schema lacks 'properties'.");
                Assert.Equal(JsonValueKind.Object, props.ValueKind);
            }
        }

        [Fact]
        public void WriteTools_CarryTheCommonLayerPropsNoteSeverityFields()
        {
            var byName = ToolRegistry.BuildAll().ToDictionary(t => t.Function.Name);

            // create_circle is a WRITE tool, so WriteTool() must have augmented it with the common fields.
            var props = byName[ToolNames.CreateCircle].Function.Parameters.GetProperty("properties");
            foreach (var common in new[] { "layer", "props", "note", "severity" })
                Assert.True(props.TryGetProperty(common, out _), $"create_circle should expose '{common}'.");

            // The control tool emit_changeset is NOT a write tool and should not carry them.
            var emit = byName[ToolNames.EmitChangeset].Function.Parameters.GetProperty("properties");
            Assert.False(emit.TryGetProperty("layer", out _));
        }

        [Fact]
        public void RequiredArrays_ReferenceDeclaredProperties()
        {
            foreach (var tool in ToolRegistry.BuildAll(includeCommandTool: true))
            {
                var schema = tool.Function.Parameters;
                if (!schema.TryGetProperty("required", out var required)) continue;

                var declared = schema.GetProperty("properties");
                foreach (var req in required.EnumerateArray())
                {
                    string name = req.GetString()!;
                    Assert.True(declared.TryGetProperty(name, out _),
                        $"{tool.Function.Name} marks '{name}' required but doesn't declare it.");
                }
            }
        }
    }
}
