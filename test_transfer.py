import io
import random
import tempfile
import unittest
import hashlib
import struct
import zlib
from pathlib import Path

from PIL import Image, ImageDraw, ImageEnhance

import ecc
import transfer as t
import rgbframe
from probe import measure
from desktop import fit_size, resolve_size


class ErrorCorrectionTests(unittest.TestCase):
    def test_correct_up_to_sixteen_bytes(self):
        rng = random.Random(123)
        for count in (0, 1, 8, 16):
            for _ in range(8):
                data = rng.randbytes(223)
                wire = bytearray(ecc.encode(data))
                for index in rng.sample(range(255), count):
                    wire[index] ^= rng.randrange(1, 256)
                self.assertEqual(ecc.decode(wire), data)


class EncryptionTests(unittest.TestCase):
    def test_empty_compressible_and_binary(self):
        for data in (b'', b'abcd' * 10000, random.Random(1).randbytes(1200)):
            with self.subTest(size=len(data)):
                self.assertEqual(t.decrypt(t.encrypt(data, '密碼 test'), '密碼 test'), data)

    def test_randomized_encryption(self):
        self.assertNotEqual(t.encrypt(b'file', 'password'), t.encrypt(b'file', 'password'))

    def test_wrong_password_and_tampering(self):
        envelope = t.encrypt(b'file', 'password')
        with self.assertRaises(ValueError):
            t.decrypt(envelope, 'wrong')
        changed = bytearray(envelope)
        changed[-1] ^= 1
        with self.assertRaises(ValueError):
            t.decrypt(bytes(changed), 'password')

    def test_missing_duplicate_reordered_and_mixed(self):
        data = random.Random(2).randbytes(t.CHUNK * 2)
        envelope = t.encrypt(data, 'password')
        packets = list(t.packets(envelope))
        collector = t.Collector()
        collector.add(packets[-1])
        collector.add(packets[-1])
        self.assertEqual(len(collector.parts), 1)
        with self.assertRaises(ValueError):
            collector.assemble()
        for packet in reversed(packets[:-1]):
            collector.add(packet)
        self.assertEqual(t.decrypt(collector.assemble(), 'password'), data)
        with self.assertRaises(ValueError):
            collector.add(next(t.packets(t.encrypt(b'other', 'password'))))

    def test_crc_rejects_damage(self):
        packet = bytearray(next(t.packets(t.encrypt(b'file', 'password'))))
        packet[-10] ^= 1
        collector = t.Collector()
        self.assertFalse(collector.add(bytes(packet)))
        self.assertIsNone(collector.transfer_id)


class ImageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = random.Random(42).randbytes(40000)
        cls.envelope = t.encrypt(cls.data, 'password')
        cls.image = t.render_packet(next(t.packets(cls.envelope)))

    def check_image(self, image):
        collector = t.Collector()
        t.scan(image, collector)
        self.assertTrue(collector.complete)
        self.assertEqual(t.decrypt(collector.assemble(), 'password'), self.data)

    def test_original(self):
        self.check_image(self.image)

    def test_scaled(self):
        for size in ((1440, 810), (960, 540), (1600, 900)):
            with self.subTest(size=size):
                self.check_image(self.image.resize(size, Image.Resampling.BILINEAR))

    def test_jpeg_chroma_subsampling(self):
        for quality in (95, 85, 70):
            with self.subTest(quality=quality):
                buffer = io.BytesIO()
                self.image.save(buffer, format='JPEG', quality=quality, subsampling=2)
                buffer.seek(0)
                with Image.open(buffer) as image:
                    self.check_image(image)

    def test_calibrated_brightness(self):
        self.check_image(ImageEnhance.Brightness(self.image).enhance(0.8))

    def test_screenshot_offset_and_scale(self):
        desktop = Image.new('RGB', (2200, 1400), (75, 85, 95))
        desktop.paste(self.image.resize((1440, 810), Image.Resampling.BILINEAR), (123, 97))
        self.check_image(desktop)

    def test_local_damage(self):
        damaged = self.image.copy()
        ImageDraw.Draw(damaged).rectangle((800, 400, 831, 431), fill='white')
        self.check_image(damaged)

    def test_missing_marker_rejected(self):
        damaged = self.image.copy()
        ImageDraw.Draw(damaged).rectangle((0, 0, 63, 63), fill='white')
        collector = t.Collector()
        t.scan(damaged, collector)
        self.assertFalse(collector.complete)

    def test_frame_files_and_no_overwrite(self):
        with tempfile.TemporaryDirectory() as temp:
            destination = Path(temp) / 'frames'
            paths = t.write_frames(self.envelope, destination)
            self.assertEqual(len(paths), 1)
            with Image.open(paths[0]) as image:
                self.check_image(image)
            with self.assertRaises(FileExistsError):
                t.write_frames(self.envelope, destination)


class RGBTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = random.Random(150).randbytes(4_000_000)
        cls.envelope = t.encrypt(cls.data, 'rgb password')
        cls.image = t.render_packet(next(t.packets(cls.envelope, 'rgb')))

    def test_four_megabyte_native_round_trip(self):
        collector = t.Collector()
        t.scan(self.image, collector, 'rgb')
        self.assertEqual(t.decrypt(collector.assemble(), 'rgb password'), self.data)

    def test_native_screenshot_with_offset(self):
        desktop = Image.new('RGB', (2150, 1300), (75, 85, 95))
        desktop.paste(self.image, (79, 93))
        collector = t.Collector()
        t.scan(desktop, collector, 'rgb')
        self.assertEqual(collector.assemble(), self.envelope)

    def test_changed_data_byte_rejected(self):
        damaged = self.image.copy()
        r, g, b = damaged.getpixel((900, 500))
        damaged.putpixel((900, 500), (r ^ 1, g, b))
        collector = t.Collector()
        t.scan(damaged, collector, 'rgb')
        self.assertFalse(collector.complete)

    def test_scaled_and_jpeg_rejected(self):
        compressed = io.BytesIO()
        self.image.save(compressed, format='JPEG', quality=95, subsampling=0)
        compressed.seek(0)
        for image in (self.image.resize((1440, 810), Image.Resampling.BILINEAR), Image.open(compressed)):
            collector = t.Collector()
            t.scan(image, collector, 'rgb')
            self.assertFalse(collector.complete)

    def test_full_capacity_and_multiple_frames(self):
        data = random.Random(151).randbytes(t.RGB_CHUNK + 100)
        envelope = t.encrypt(data, 'password')
        collector = t.Collector()
        frames = list(t.packets(envelope, 'rgb'))
        self.assertEqual(len(frames), 2)
        for packet in reversed(frames):
            t.scan(t.render_packet(packet), collector, 'rgb')
        self.assertEqual(t.decrypt(collector.assemble(), 'password'), data)

    def test_probe_counts_one_changed_channel(self):
        image = rgbframe.render(rgbframe.probe_data())
        self.assertEqual(measure(image)['changed_channels'], 0)
        r, g, b = image.getpixel((900, 500))
        image.putpixel((900, 500), (r ^ 1, g, b))
        result = measure(image)
        self.assertEqual(result['changed_channels'], 1)
        self.assertEqual(result['changed_pixels'], 1)
        self.assertEqual(result['max_error'], 1)


class VariableSizeTests(unittest.TestCase):
    def test_sizes_and_offset_screenshots(self):
        data = random.Random(80).randbytes(400000)
        envelope = t.encrypt(data, 'password')
        for size in ((640, 360), (1280, 720), (1600, 900)):
            with self.subTest(size=size):
                collector = t.Collector()
                for packet in t.packets(envelope, 'rgb', size):
                    image = t.render_packet(packet)
                    self.assertEqual(image.size, size)
                    desktop = Image.new('RGB', (size[0] + 160, size[1] + 200), (100, 100, 100))
                    desktop.paste(image, (64, 93))
                    t.scan(desktop, collector, 'rgb')
                self.assertEqual(t.decrypt(collector.assemble(), 'password'), data)

    def test_variable_probe_dimensions(self):
        size = (1280, 720)
        result = measure(rgbframe.render(rgbframe.probe_data(size), size))
        self.assertEqual((result['width'], result['height']), size)
        self.assertEqual(result['changed_channels'], 0)

    def test_wrong_declared_dimensions_rejected(self):
        envelope = t.encrypt(b'file', 'password')
        packet = next(t.packets(envelope, 'rgb', (640, 360)))
        image = rgbframe.render(packet, (1280, 720))
        collector = t.Collector()
        t.scan(image, collector, 'rgb')
        self.assertFalse(collector.complete)

    def test_mixed_dimensions_rejected(self):
        envelope = t.encrypt(b'file', 'password')
        collector = t.Collector()
        collector.add(next(t.packets(envelope, 'rgb', (640, 360))))
        with self.assertRaises(ValueError):
            collector.add(next(t.packets(envelope, 'rgb', (1280, 720))))

    def test_legacy_rgb_packet(self):
        envelope = t.encrypt(b'legacy file', 'password')
        digest = hashlib.sha256(envelope).digest()[:16]
        packet = t.RGB_PACKET.pack(b'PTR3', digest, 0, 1, len(envelope)) + envelope
        packet += struct.pack('!I', zlib.crc32(packet))
        collector = t.Collector()
        t.scan(t.render_packet(packet), collector, 'rgb')
        self.assertEqual(collector.assemble(), envelope)

    def test_size_bounds_and_auto_margins(self):
        self.assertEqual(resolve_size('1280x720'), (1280, 720))
        self.assertEqual(fit_size((1920, 1040)), (1600, 900))
        size = fit_size((1366, 728))
        self.assertLessEqual(size[0] + 80, 1366)
        self.assertLessEqual(size[1] + 120, 728)
        self.assertEqual(size[0] * 9, size[1] * 16)
        for invalid in ('bad', '100x100', '10000x10000'):
            with self.assertRaises(ValueError):
                resolve_size(invalid)


if __name__ == '__main__':
    unittest.main()
