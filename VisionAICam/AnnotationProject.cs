using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VisionAICam.Pages;

namespace VisionAICam
{
   
    public class AnnotationProject
    {
        public required string ProjectName { get; set; }
        public List<string> ImagePaths { get; set; } = new();
        public List<string> ClassLabels { get; set; } = new();
        public List<AnnotationRecord> Annotations { get; set; } = new();
        public int SelectedImageIndex { get; set; } = 0;

    }
}
