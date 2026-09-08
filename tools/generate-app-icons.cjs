const fs = require('fs');
const path = require('path');
const sharp = require('sharp');

const workspaceRoot = path.resolve(__dirname, '..', '..');
const sourcePath = path.join(workspaceRoot, 'icons', 'app-logo.svg');
const assetsDirectory = path.resolve(__dirname, '..', 'PyRunner', 'Assets');
const iconSizes = [16, 24, 32, 48, 64, 128, 256];

async function render(size) {
  return sharp(sourcePath, { density: 384 })
    .resize(size, size)
    .png()
    .toBuffer();
}

async function main() {
  fs.mkdirSync(assetsDirectory, { recursive: true });

  const images = [];
  for (const size of iconSizes) {
    images.push({ size, data: await render(size) });
  }

  fs.writeFileSync(
    path.join(assetsDirectory, 'Square44x44Logo.png'),
    await render(44));
  fs.writeFileSync(
    path.join(assetsDirectory, 'Square150x150Logo.png'),
    await render(150));

  const directorySize = 6 + images.length * 16;
  const outputSize = directorySize + images.reduce((total, image) => total + image.data.length, 0);
  const icon = Buffer.alloc(outputSize);
  icon.writeUInt16LE(0, 0);
  icon.writeUInt16LE(1, 2);
  icon.writeUInt16LE(images.length, 4);

  let imageOffset = directorySize;
  images.forEach((image, index) => {
    const entryOffset = 6 + index * 16;
    icon.writeUInt8(image.size === 256 ? 0 : image.size, entryOffset);
    icon.writeUInt8(image.size === 256 ? 0 : image.size, entryOffset + 1);
    icon.writeUInt8(0, entryOffset + 2);
    icon.writeUInt8(0, entryOffset + 3);
    icon.writeUInt16LE(1, entryOffset + 4);
    icon.writeUInt16LE(32, entryOffset + 6);
    icon.writeUInt32LE(image.data.length, entryOffset + 8);
    icon.writeUInt32LE(imageOffset, entryOffset + 12);
    image.data.copy(icon, imageOffset);
    imageOffset += image.data.length;
  });

  fs.writeFileSync(path.join(assetsDirectory, 'AppIcon.ico'), icon);
  process.stdout.write(`Generated PyRunner icons in ${assetsDirectory}\n`);
}

main().catch((error) => {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});
