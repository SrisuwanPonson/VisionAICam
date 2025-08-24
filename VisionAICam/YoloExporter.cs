using System.IO;
using System.Windows;
using System.Linq;
using System.Collections.Generic;
using VisionAICam.Pages;

namespace VisionAICam
{
    public enum YoloExportFormat
    {
        YoloV5,
        YoloV8
    }
    public static class YoloExporter
    {
        public static void ExportWithSplit(
        AnnotationProject project,
        string outputFolder,
        Func<string, Size> getImageSize,
        double trainRatio = 0.7,
        double valRatio = 0.2,
        double testRatio = 0.1,
        YoloExportFormat exportFormat = YoloExportFormat.YoloV8)
        {
            if (project == null || project.ImagePaths.Count == 0)
                return;
           
            
            // Clean and create output directory
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, recursive: true);
            }
            Directory.CreateDirectory(outputFolder);

            var (train, val, test) = DatasetSplitter.Split(project.ImagePaths, trainRatio, valRatio, testRatio);

            ExportSet(project, train, Path.Combine(outputFolder, "train"), getImageSize, exportFormat);
            ExportSet(project, val, Path.Combine(outputFolder, "valid"), getImageSize, exportFormat);
            ExportSet(project, test, Path.Combine(outputFolder, "test"), getImageSize, exportFormat);



            //File.WriteAllLines(Path.Combine(outputFolder, "classes.txt"), project.ClassLabels);
            WriteDataYaml(outputFolder, project.ClassLabels, project.ProjectName);
        }


        private static void ExportSet(
    AnnotationProject project,
    List<string> imagePaths,
    string setFolder,
    Func<string, Size> getImageSize,
    YoloExportFormat exportFormat)
        {
            var imagesFolder = Path.Combine(setFolder, "image");
            var labelsFolder = Path.Combine(setFolder, "labels");
            Directory.CreateDirectory(imagesFolder);
            Directory.CreateDirectory(labelsFolder);

            var imageFileNames = new HashSet<string>(
                imagePaths.Select(p => Path.GetFileName(p)),
                StringComparer.OrdinalIgnoreCase);

            var annotations = project.Annotations
                .Where(a => imageFileNames.Contains(a.ImageName))
                .Where(a => a.AnnotationType == AnnotationType.Rectangle)
                .GroupBy(a => a.ImageName);

            foreach (var group in annotations)
            {
                var imageSize = getImageSize(group.Key);
                if (imageSize.Width == 0 || imageSize.Height == 0)
                    continue;

                var lines = group
                    .Select(a => a.ToYoloFormat(imageSize, project.ClassLabels, exportFormat))
                    .Where(line => !string.IsNullOrEmpty(line))
                    .ToList();

                // Use the same name as the image, but with .txt extension
                var imageFileName = Path.GetFileName(group.Key);
                var labelFileName = Path.ChangeExtension(imageFileName, ".txt");
                var labelFile = Path.Combine(labelsFolder, labelFileName);
                File.WriteAllLines(labelFile, lines);

                // Copy the image file to the image folder
                var srcImagePath = project.ImagePaths.FirstOrDefault(p => Path.GetFileName(p) == imageFileName);
                if (!string.IsNullOrEmpty(srcImagePath))
                {
                    var destImagePath = Path.Combine(imagesFolder, imageFileName);
                    if (!File.Exists(destImagePath))
                        File.Copy(srcImagePath, destImagePath, overwrite: false);
                }
            }
        }


        private static void WriteDataYaml(string outputFolder, List<string> classLabels, string projectName)
        {
            var classNames = string.Join(", ", classLabels.Select(n => $"'{n}'"));
            var yaml = $@"
train: ../train/image
val: ../valid/image
test: ../test/image

nc: {classLabels.Count}
names: [{classNames}]

roboflow:
  workspace: 
  project: {projectName}
  version: 
  license: 
  url: 
";
            File.WriteAllText(Path.Combine(outputFolder, "data.yaml"), yaml.Trim());
        }






    }
}
