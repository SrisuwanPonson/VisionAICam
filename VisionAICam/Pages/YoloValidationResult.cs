using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisionAICam.Pages
{
    public class YoloValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
    }
}
