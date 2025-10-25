using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ClearEngine.DataSet
{
    public static class ProjectConverter
    {
        // Convert rectangle/polygon annotations to rotated-box annotations (DTO types)
        public static AnnotationProjectDto ConvertToRotatedBox(AnnotationProjectDto project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));

            var rotatedAnnotations = new List<AnnotationDto>();

            foreach (var ann in project.Annotations)
            {
                List<double>? values = null;

                if (ann.AnnotationType == AnnotationType.Rectangle && ann.Points.Count == 2)
                {
                    var p0 = ann.Points[0];
                    var p1 = ann.Points[1];

                    double x1 = Math.Min(p0.X, p1.X);
                    double y1 = Math.Min(p0.Y, p1.Y);
                    double x2 = Math.Max(p0.X, p1.X);
                    double y2 = Math.Max(p0.Y, p1.Y);

                    double cx = (x1 + x2) / 2.0;
                    double cy = (y1 + y2) / 2.0;
                    double w = Math.Abs(x2 - x1);
                    double h = Math.Abs(y2 - y1);
                    double angle = 0.0;

                    values = GeometryUtils.GetRotatedBoxAs8Values(cx, cy, w, h, angle);
                }
                else if (ann.AnnotationType == AnnotationType.Polygon && ann.Points.Count > 2)
                {
                    var pts = ann.Points.Select(p => p).ToList();
                    var rect = GeometryUtils.GetMinAreaRect(pts);
                    values = GeometryUtils.GetRotatedBoxAs8Values(rect.Center.X, rect.Center.Y, rect.Size.Width, rect.Size.Height, rect.Angle);
                }

                if (values != null && values.Count == 8)
                {
                    var points = new List<PointF>
                    {
                        new PointF((float)values[0], (float)values[1]),
                        new PointF((float)values[2], (float)values[3]),
                        new PointF((float)values[4], (float)values[5]),
                        new PointF((float)values[6], (float)values[7])
                    };

                    rotatedAnnotations.Add(new AnnotationDto
                    {
                        ImageName = ann.ImageName,
                        Label = ann.Label,
                        AnnotationType = AnnotationType.RotatedBox,
                        RawValues = values,
                        Points = points
                    });
                }
                else
                {
                    rotatedAnnotations.Add(ann);
                }
            }

            return new AnnotationProjectDto
            {
                ProjectName = project.ProjectName,
                ImagePaths = project.ImagePaths.ToList(),
                ClassLabels = project.ClassLabels.ToList(),
                Annotations = rotatedAnnotations,
                SelectedImageIndex = project.SelectedImageIndex
            };
        }
    }
}