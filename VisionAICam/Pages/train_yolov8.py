import logging
import os
import time

# Resolve script directory
script_dir = os.path.dirname(os.path.abspath(__file__))
log_path = os.path.join(script_dir, "training.log")

# Setup logger
logger = logging.getLogger("TrainerLogger")
logger.setLevel(logging.DEBUG)

# Console handler
console_handler = logging.StreamHandler()
console_handler.setLevel(logging.INFO)

# File handler (writes to same folder as script)
file_handler = logging.FileHandler(log_path, mode='w')
file_handler.setLevel(logging.DEBUG)

# Formatter
formatter = logging.Formatter("[%(asctime)s] %(levelname)s: %(message)s", "%H:%M:%S")
console_handler.setFormatter(formatter)
file_handler.setFormatter(formatter)

# Add handlers
logger.addHandler(console_handler)
logger.addHandler(file_handler)

# Simulated training steps
def simulate_training():
    logger.info("🚀 Training started")
    for epoch in range(1, 4):
        logger.debug(f"Epoch {epoch}: initializing...")
        time.sleep(1)
        logger.info(f"✅ Epoch {epoch} completed")
    logger.warning("⚠️ Training finished with minor warnings")
    logger.error("❌ Simulated error for demonstration")
    logger.info("🔍 Training script completed")

if __name__ == "__main__":
    simulate_training()