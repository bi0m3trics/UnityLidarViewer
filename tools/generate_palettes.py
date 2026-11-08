"""Generate the viewer's palette atlas in menu order."""
from pathlib import Path
import matplotlib
import numpy as np
from PIL import Image

out = Path(__file__).resolve().parents[1] / 'UnityProject/Assets/Lidar/Resources'
out.mkdir(parents=True, exist_ok=True)
samples = np.linspace(0, 1, 256)
rows = [matplotlib.colormaps[name](samples)[:, :3]
        for name in ['magma', 'viridis', 'plasma', 'inferno', 'cividis']]
stops = np.array([[0, 0, 1], [0, 1, 1], [0, 1, 0], [1, 1, 0], [1, .5, 0], [1, 0, 0]])
rows.append(np.column_stack([np.interp(samples, np.linspace(0, 1, 6), channel) for channel in stops.T]))
rows.extend(matplotlib.colormaps[name](samples)[:, :3] for name in ['tab20', 'turbo', 'coolwarm', 'gray'])
# PNG rows start at the top. The shader addresses atlas rows from the bottom.
Image.fromarray(np.round(np.array(rows[::-1]) * 255).astype(np.uint8)).save(out / 'LidarPalettes.png')
