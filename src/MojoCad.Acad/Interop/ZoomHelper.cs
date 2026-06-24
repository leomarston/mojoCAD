using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace MojoCad.Acad.Interop
{
    /// <summary>Zooms the active viewport to frame an extents box (plan-view / WCS assumption).</summary>
    internal static class ZoomHelper
    {
        public static void ZoomToExtents(Editor ed, Extents3d ext)
        {
            const double margin = 1.1; // a little padding around the target
            using ViewTableRecord view = ed.GetCurrentView();

            double width = (ext.MaxPoint.X - ext.MinPoint.X) * margin;
            double height = (ext.MaxPoint.Y - ext.MinPoint.Y) * margin;
            if (width <= 0) width = 1.0;
            if (height <= 0) height = 1.0;

            // Keep the current aspect ratio so the view does not distort.
            double aspect = view.Height <= 0 ? 1.0 : view.Width / view.Height;
            if (width / height > aspect) height = width / aspect;
            else width = height * aspect;

            view.Width = width;
            view.Height = height;
            view.CenterPoint = new Point2d(
                (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0);
            ed.SetCurrentView(view);
        }
    }
}
