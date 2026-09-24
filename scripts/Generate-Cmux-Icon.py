"""Generate the cmux Windows icon and MSIX tile assets."""

from pathlib import Path
from PIL import Image, ImageDraw


root = Path(__file__).resolve().parents[1]
assets = root / "packaging" / "Assets"
assets.mkdir(parents=True, exist_ok=True)

size = 256
image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((4, 4, 252, 252), radius=48, fill="#1b1c1e")
draw.rounded_rectangle((19, 19, 237, 237), radius=34, outline="#5a5b5d", width=4)

# A terminal prompt drawn on an 8px grid stays legible at title-bar size.
draw.line([(70, 89), (112, 126), (70, 163)], fill="#f2f1ed", width=20, joint="curve")
draw.rounded_rectangle((123, 157, 184, 177), radius=7, fill="#f2f1ed")
draw.ellipse((180, 65, 206, 91), fill="#d71921")

image.save(assets / "Cmux.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (256, 256)])
for name, edge in (("Logo.png", 50), ("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150)):
    image.resize((edge, edge), Image.Resampling.LANCZOS).save(assets / name)
