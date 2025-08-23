using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisionAICam
{
    public static class ProjectSession
    {
        public static AnnotationProject? CurrentProject { get; set; }
        public static List<AnnotationRecord> Annotations { get; set; } = new();
        public static List<string> ImagePaths { get; set; } = new();
        public static int CurrentImageIndex { get; set; } = -1;
        public static string? CurrentImagePath { get; set; }
    }
}
