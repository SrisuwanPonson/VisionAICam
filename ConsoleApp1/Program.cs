using System;
using OpenCvSharp;

class Program
{
    static void Main()
    {
        using var cap = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
        if (!cap.IsOpened())
        {
            Console.WriteLine("No camera detected.");
            return;
        }
        Console.WriteLine("Camera detected. Press ESC to quit.");
        using var window = new Window("Camera");
        var mat = new Mat();
        while (true)
        {
            cap.Read(mat);
            if (mat.Empty())
                break;
            window.ShowImage(mat);
            if (Cv2.WaitKey(1) == 27) // ESC
                break;
        }
    }
}