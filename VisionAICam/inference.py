from ultralytics import YOLO
from PIL import Image
import io

model = None
model_path = None

def load_model(path):
    global model, model_path
    if model is None or model_path != path:
        model = YOLO(path)
        model_path = path

def detect(image_bytes, model_path_arg=None):
    if model_path_arg is not None:
        load_model(model_path_arg)
    elif model is None:
        raise RuntimeError("Model path must be provided on first call.")

    image = Image.open(io.BytesIO(image_bytes)).convert("RGB")
    results = model(image)

    detections = []
    for r in results:
        boxes = r.boxes.xyxy.cpu().numpy()
        confs = r.boxes.conf.cpu().numpy()
        classes = r.boxes.cls.cpu().numpy()
        for box, conf, cls in zip(boxes, confs, classes):
            class_idx = int(cls)
            class_name = model.names[class_idx] if hasattr(model, "names") else str(class_idx)
            detections.append({
                "class": class_name,
                "confidence": float(conf),
                "box": [float(box[0]), float(box[1]), float(box[2]), float(box[3])]
            })
    return detections
