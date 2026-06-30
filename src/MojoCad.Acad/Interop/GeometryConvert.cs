using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MojoCad.Core.Geometry;

namespace MojoCad.Acad.Interop
{
    /// <summary>Converts between the AutoCAD-free Core geometry types and Autodesk's geometry types.</summary>
    internal static class GeometryConvert
    {
        public static Point3d ToPoint3d(Pt p) => new Point3d(p.X, p.Y, p.Z);

        public static Point2d ToPoint2d(Pt p) => new Point2d(p.X, p.Y);

        public static Pt ToPt(Point3d p) => new Pt(p.X, p.Y, p.Z);

        public static Pt ToPt(Point2d p) => new Pt(p.X, p.Y, 0);

        public static Vector3d ToVector3d(Vec v) => new Vector3d(v.X, v.Y, v.Z);

        public static Vec ToVec(Vector3d v) => new Vec(v.X, v.Y, v.Z);

        public static Bounds ToBounds(Extents3d e) => new Bounds(ToPt(e.MinPoint), ToPt(e.MaxPoint));

        public static double DegToRad(double deg) => deg * Angles.DegToRad;
    }
}
