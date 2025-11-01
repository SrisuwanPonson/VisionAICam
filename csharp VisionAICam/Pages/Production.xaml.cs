// Add inside Production class
private void OnFrameReady(BitmapSource bitmap)
{
    try
    {
        // 1) Set image safely (freezing + UI-thread assign)
        VisionAICam.Helpers.UiHelpers.SetImageSourceSafe(CameraImage, bitmap);

        // 2) Update overlays without recreating shapes each frame:
        //    - Keep a pool/list of Rectangle/TextBlock already added to BoundingCanvas
        //    - Update their Canvas.Left/Top/Width/Height/Text instead of clearing and re-adding
        // Example (pseudo-code; adapt to your detection results):
        /*
        var detections = currentDetections; // get from inference result
        UpdateOverlayRectangles(detections);
        */
    }
    catch (Exception ex)
    {
        // swallow or log - don't block UI
        _logger?.LogInfo($"OnFrameReady error: {ex.Message}");
    }
}