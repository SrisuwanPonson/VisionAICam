from flask import Flask, request, jsonify
import torch
import cv2
import numpy as np

app = Flask(__name__)

# Load your PyTorch model
# Replace 'model.pt' with the path to your .pt model file
model = torch.jit.load('model.pt')
model.eval()  # Set the model to evaluation mode

# Function to preprocess the image for the model
def preprocess_image(image):
    # Resize or normalize the image as required by your model
    # Example: Convert BGR to RGB and normalize
    image = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
    image = torch.from_numpy(image).permute(2, 0, 1).float() / 255.0  # HWC to CHW and normalize
    image = image.unsqueeze(0)  # Add batch dimension
    return image

# Function to postprocess the model's output
def postprocess_output(output):
    # Replace this with your model's specific output processing logic
    # Example: Convert model output to a list of detections
    detections = []
    for det in output[0]:  # Assuming output[0] contains detections
        if det[-1] > 0.5:  # Confidence threshold
            detections.append({
                "class": int(det[5]),  # Class ID
                "confidence": float(det[-1]),  # Confidence score
                "box": [float(det[0]), float(det[1]), float(det[2]), float(det[3])]  # Bounding box
            })
    return detections

@app.route('/detect', methods=['POST'])
def detect():
    if 'file' not in request.files:
        return jsonify({"error": "No file provided"}), 400

    file = request.files['file']
    if file.filename == '':
        return jsonify({"error": "No file selected"}), 400

    try:
        # Read the image file as a NumPy array
        file_bytes = np.frombuffer(file.read(), np.uint8)
        image = cv2.imdecode(file_bytes, cv2.IMREAD_COLOR)

        # Preprocess the image for the model
        input_tensor = preprocess_image(image)

        # Perform inference
        with torch.no_grad():
            output = model(input_tensor)

        # Postprocess the model's output
        detections = postprocess_output(output)

        # Return detections as JSON
        return jsonify({"detections": detections})
    except Exception as e:
        return jsonify({"error": str(e)}), 500

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=8000)