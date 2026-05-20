import os
import sys
import subprocess

def main(image_path):
    # GPU 에러 방지 (CPU 모드 강제)
    os.environ["CUDA_VISIBLE_DEVICES"] = "-1"
    
    # oemer 실행
    print(f"Analyzing {image_path}...")
    subprocess.run(["python", "-m", "oemer", image_path], check=True)

if __name__ == "__main__":
    if len(sys.argv) > 1:
        main(sys.argv[1])