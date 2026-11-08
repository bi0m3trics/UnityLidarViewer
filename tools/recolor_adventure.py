"""Generate local albedo textures from an installed Adventure Character package."""

from pathlib import Path

from PIL import Image, ImageOps


def main():
    assets = Path(__file__).resolve().parents[1] / "UnityProject" / "Assets"
    source = assets / "Adventure_Character" / "Textures"
    output = assets / "Lidar" / "Textures" / "Adventure"
    output.mkdir(parents=True, exist_ok=True)

    colors = [
        ("DarkOrangeJacket", "Up", "#451909", "#bf511b", "#e98b48", 73),
        ("KhakiPants", "Down", "#393223", "#aa9565", "#d9c69c", 97),
    ]
    for name, atlas, dark, middle, light, midpoint in colors:
        texture = source / f"Man_Cloth_{atlas}_Albedo_01.tga"
        with Image.open(texture) as image:
            tone = ImageOps.grayscale(image.convert("RGB"))
            recolored = ImageOps.colorize(
                tone, dark, light, middle, blackpoint=13, whitepoint=211, midpoint=midpoint
            )
            recolored.save(output / f"{name}.png")


if __name__ == "__main__":
    main()
