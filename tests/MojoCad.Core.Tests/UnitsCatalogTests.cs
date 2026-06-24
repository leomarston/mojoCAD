using MojoCad.Core.Drawing;
using Xunit;

namespace MojoCad.Core.Tests
{
    /// <summary>
    /// UnitsCatalog maps AutoCAD's INSUNITS code to a label and a "drawing-units per metre" factor, which
    /// the system prompt uses to let the agent convert human dimensions correctly. Getting the factor or
    /// the unknown-code fallback wrong would make the agent draw at the wrong scale, so we pin the common
    /// codes and the out-of-range fallback.
    /// </summary>
    public sealed class UnitsCatalogTests
    {
        [Fact]
        public void Millimeters_AreOneThousandPerMeter()
        {
            var def = UnitsCatalog.Resolve(4);
            Assert.Equal("Millimeters", def.Label);
            Assert.Equal(1000.0, def.PerMeter, 6);
        }

        [Fact]
        public void Inches_UseTheImperialFactor()
        {
            var def = UnitsCatalog.Resolve(1);
            Assert.Equal("Inches", def.Label);
            Assert.Equal(39.37007874, def.PerMeter, 6);
        }

        [Fact]
        public void Meters_AreOnePerMeter()
        {
            var def = UnitsCatalog.Resolve(6);
            Assert.Equal("Meters", def.Label);
            Assert.Equal(1.0, def.PerMeter, 9);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(99)]
        [InlineData(1000)]
        public void UnknownCode_FallsBackToUnitless(int code)
        {
            var def = UnitsCatalog.Resolve(code);
            Assert.Equal("Unitless", def.Label);
            Assert.Equal(1.0, def.PerMeter, 9);
        }

        [Fact]
        public void Unitless_ZeroCode_IsUnitless()
        {
            var def = UnitsCatalog.Resolve(0);
            Assert.Equal("Unitless", def.Label);
            Assert.Equal(1.0, def.PerMeter, 9);
        }
    }
}
