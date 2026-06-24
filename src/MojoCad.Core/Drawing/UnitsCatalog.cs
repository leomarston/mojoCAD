using System.Collections.Generic;

namespace MojoCad.Core.Drawing
{
    /// <summary>
    /// Maps AutoCAD's INSUNITS code to a human label and a "drawing units per metre" factor. Shared by
    /// the Acad adapter (when building <see cref="DrawingSummary"/>) and the system-prompt builder, so
    /// the agent is always told the true units and can convert human dimensions itself.
    /// </summary>
    public static class UnitsCatalog
    {
        public readonly struct UnitDef
        {
            public string Label { get; }
            public double PerMeter { get; }
            public UnitDef(string label, double perMeter) { Label = label; PerMeter = perMeter; }
        }

        // INSUNITS values per the AutoCAD documentation. PerMeter = how many drawing units make one metre.
        private static readonly Dictionary<int, UnitDef> Map = new Dictionary<int, UnitDef>
        {
            { 0,  new UnitDef("Unitless", 1.0) },
            { 1,  new UnitDef("Inches", 39.37007874) },
            { 2,  new UnitDef("Feet", 3.280839895) },
            { 3,  new UnitDef("Miles", 0.000621371) },
            { 4,  new UnitDef("Millimeters", 1000.0) },
            { 5,  new UnitDef("Centimeters", 100.0) },
            { 6,  new UnitDef("Meters", 1.0) },
            { 7,  new UnitDef("Kilometers", 0.001) },
            { 8,  new UnitDef("Microinches", 39370078.74) },
            { 9,  new UnitDef("Mils", 39370.07874) },
            { 10, new UnitDef("Yards", 1.093613298) },
            { 11, new UnitDef("Angstroms", 1.0e10) },
            { 12, new UnitDef("Nanometers", 1.0e9) },
            { 13, new UnitDef("Microns", 1.0e6) },
            { 14, new UnitDef("Decimeters", 10.0) },
            { 15, new UnitDef("Decameters", 0.1) },
            { 16, new UnitDef("Hectometers", 0.01) },
            { 17, new UnitDef("Gigameters", 1.0e-9) },
            { 18, new UnitDef("Astronomical Units", 6.6846e-12) },
            { 19, new UnitDef("Light Years", 1.057e-16) },
            { 20, new UnitDef("Parsecs", 3.241e-17) },
            { 21, new UnitDef("US Survey Feet", 3.280833333) },
            { 22, new UnitDef("US Survey Inch", 39.36996) },
            { 23, new UnitDef("US Survey Yard", 1.093611) },
            { 24, new UnitDef("US Survey Mile", 0.000621369) }
        };

        public static UnitDef Resolve(int insUnits) =>
            Map.TryGetValue(insUnits, out var def) ? def : new UnitDef("Unitless", 1.0);
    }
}
