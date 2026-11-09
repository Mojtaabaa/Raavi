/** Largest picture a user may pick before it is shrunk (the server accepts far less; this only guards the browser). */
export const MAX_PICK_BYTES = 15 * 1024 * 1024

const ACCEPTED = ['image/png', 'image/jpeg', 'image/webp']

export const isAcceptedPicture = (file: Pick<File, 'type'>): boolean => ACCEPTED.includes(file.type)

/**
 * Centre-crops a picture to a square and scales it to `size` pixels, in the browser, so the upload is small (tens of kilobytes) and uniform.
 * Transparent areas become white (the result is WebP when the browser can encode it, else JPEG). Rejects when the file is not a decodable picture.
 */
export async function squareAvatarBlob(file: Blob, size = 256): Promise<Blob> {
  const bitmap = await createImageBitmap(file)
  try {
    const side = Math.min(bitmap.width, bitmap.height)
    if (side < 1) throw new Error('empty picture')
    const canvas = document.createElement('canvas')
    canvas.width = size
    canvas.height = size
    const context = canvas.getContext('2d')
    if (!context) throw new Error('canvas unavailable')
    context.fillStyle = '#ffffff'
    context.fillRect(0, 0, size, size)
    context.imageSmoothingQuality = 'high'
    context.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, size, size)
    const encode = (type: string, quality: number) => new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, type, quality))
    const webp = await encode('image/webp', 0.9)
    const blob = webp && webp.type === 'image/webp' ? webp : await encode('image/jpeg', 0.9)
    if (!blob) throw new Error('could not encode the picture')
    return blob
  } finally {
    bitmap.close()
  }
}
