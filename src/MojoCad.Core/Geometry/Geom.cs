using System;

namespace MojoCad.Core.Geometry
{
    /// <summary>
    /// A point / coordinate in drawing units. AutoCAD-free so the domain layer
    /// never references Autodesk types. The Acad adapter converts to/from
    /// <c>Autodesk.AutoCAD.Geometry.Point3d</c>.
    /// </summary>
    public readonly struct Pt : IEquatable<Pt>
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public Pt(double x, double y, double z = 0.0)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Build a point from a JSON-style coordinate array [x,y] or [x,y,z].</summary>
        public static Pt FromArray(double[] a)
        {
            if (a == null || a.Length < 2)
                throw new ArgumentException("A coordinate needs at least [x, y].", nameof(a));
            return new Pt(a[0], a[1], a.Length > 2 ? a[2] : 0.0);
        }

        public double[] ToArray() => new[] { X, Y, Z };

        public double DistanceTo(Pt o)
        {
            double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public Pt Add(Vec v) => new Pt(X + v.X, Y + v.Y, Z + v.Z);

        public bool Equals(Pt other) =>
            Math.Abs(X - other.X) < 1e-9 &&
            Math.Abs(Y - other.Y) < 1e-9 &&
            Math.Abs(Z - other.Z) < 1e-9;

        public override bool Equals(object? obj) => obj is Pt p && Equals(p);

        public override int GetHashCode()
        {
            // Manual combine: System.HashCode isn't available on .NET Framework 4.8.
            unchecked
            {
                int h = 17;
                h = h * 31 + X.GetHashCode();
                h = h * 31 + Y.GetHashCode();
                h = h * 31 + Z.GetHashCode();
                return h;
            }
        }
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    /// <summary>A displacement vector in drawing units.</summary>
    public readonly struct Vec
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public Vec(double x, double y, double z = 0.0)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec Between(Pt from, Pt to) => new Vec(to.X - from.X, to.Y - from.Y, to.Z - from.Z);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public double[] ToArray() => new[] { X, Y, Z };
    }

    /// <summary>An axis-aligned bounding box in drawing units.</summary>
    public readonly struct Bounds
    {
        public Pt Min { get; }
        public Pt Max { get; }

        public Bounds(Pt min, Pt max)
        {
            Min = min;
            Max = max;
        }

        public double Width => Max.X - Min.X;
        public double Height => Max.Y - Min.Y;
        public Pt Center => new Pt((Min.X + Max.X) / 2.0, (Min.Y + Max.Y) / 2.0, (Min.Z + Max.Z) / 2.0);

        public bool IsEmpty => Width <= 0 && Height <= 0;

        public override string ToString() => $"[{Min} .. {Max}]";
    }

    /// <summary>Helpers for converting between degrees (the model/tool convention) and radians (AutoCAD).</summary>
    public static class Angles
    {
        public const double DegToRad = Math.PI / 180.0;
        public const double RadToDeg = 180.0 / Math.PI;

        public static double ToRadians(double degrees) => degrees * DegToRad;
        public static double ToDegrees(double radians) => radians * RadToDeg;

        /// <summary>Normalise an angle in degrees to the [0, 360) range.</summary>
        public static double Normalize(double degrees)
        {
            double a = degrees % 360.0;
            return a < 0 ? a + 360.0 : a;
        }
    }
}
