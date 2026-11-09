# XSurLab

โปรแกรมมิกซ์เสียงรอบทิศทางบน Windows · WPF / .NET 10 · รุ่นเริ่มต้น 0.1

## เปิดแอป

ต้องมี Windows และ .NET 10 SDK (หรือ .NET 10 Desktop Runtime สำหรับไฟล์ที่ build แล้ว)

```powershell
dotnet run --project src/XSurLab.App
```

หรือเปิด `src/XSurLab.App/bin/Release/net10.0-windows/XSurLab.exe` หลัง build

## ใช้งานได้ในรุ่นนี้

- นำเข้า WAV mono/stereo แบบ PCM16/24/32 และ float32; ดู waveform จากข้อมูลจริง
- มิกซ์สูงสุด 32 แทร็ก; gain, mute, solo, offset และ azimuth
- Stereo 2.0, 5.1 และ 7.1; ส่งออก interleaved PCM24 WAV พร้อม channel mask
- LFE send แยกต่างหาก: −60 dB = ปิด, low-pass Butterworth 120 Hz ลำดับ 2
- ฟัง stereo preview ผ่านอุปกรณ์เริ่มต้นของ Windows; ระดับฟังเริ่มที่ 50%
- บันทึก/เปิด `*.xsurlab.json` พร้อม relative media paths; ไม่มีการคัดลอกไฟล์เสียง
- ยกเลิก import/render ได้; ไม่แทนที่ไฟล์ export หาก clipping หรือยกเลิก

เริ่มด้วย **Import WAV** → เลือกแทร็ก → ปรับ gain/azimuth → **Stereo preview** หรือ **Export PCM WAV**.
การปรับมิกซ์จะหยุด preview เดิม; กดใหม่เพื่อฟังค่าล่าสุด.
Master เริ่มที่ −6 dB เพื่อเผื่อ headroom. ถ้า sample peak เกิน 0 dBFS ให้ลด gain แล้ว render ใหม่.

## ตรวจ build และเสียง

```powershell
dotnet build XSurLab.slnx -c Release
dotnet run --project tests/XSurLab.SmokeTests -c Release --no-build
```

ชุดทดสอบเป็น console harness ที่คืน exit code เมื่อไม่ผ่าน และลบไฟล์ทดสอบเมื่อจบ.
CI บน Windows รันสองคำสั่งเดียวกัน. ไม่มี dependency จาก NuGet ภายนอก.

## ขอบเขต

นี่คือ **offline PCM mixer**. ยังไม่มี live surround monitoring, ASIO/WASAPI device selection,
system-audio capture, EQ/dynamics, automation, clip editing, stems, LUFS/true-peak measurement,
DTS encoding, DTS:X authoring หรือ 7.1.4 rendering. ต้นแบบ 8 หน้ายังคงอยู่ที่
[design/xsurlab-studio.html](design/xsurlab-studio.html) เพื่อใช้พัฒนาต่อ.

Stereo monitor ใช้ Lo/Ro: C และ surround ไปด้านที่เกี่ยวข้องที่ −3.01 dB; ไม่รวม LFE.
เป็น fold-down เพื่อฟังเทียบ ไม่ใช่ binaural render หรือการยืนยันการต่อสายลำโพง.
Stereo source รักษา L/R เป็นคู่ ±30° รอบ azimuth; mono source ใช้ equal-power ระหว่างลำโพงคู่ติดกัน.
Stereo layout clamp pan ที่ ±30° และไม่มี LFE.

ไฟล์นำเข้าจำกัด data chunk ละ 64 MiB; decoded audio ทั้ง session 256 MiB; ความยาว render สูงสุด 1 ชั่วโมง
และไม่เกินขนาด RIFF 4 GiB. อัตราออก 44.1/48/96 kHz; การแปลงอัตราใช้ linear interpolation
ซึ่งยังไม่มี anti-alias filter สำหรับงาน mastering. PCM export ยังไม่ใส่ dither/BWF metadata.
Sample peak ที่รายงานไม่ใช่ true peak และไม่มี limiter/normalization อัตโนมัติ.

ดู [โครงสร้างและลำดับช่อง](docs/architecture.md). ไม่มีการรับรอง DTS หรือมาตรฐานส่งมอบภาพยนตร์ในรุ่นนี้.

## ตั้ง repo ครั้งแรกเมื่อ sandbox เขียน .git ไม่ได้

เปิด PowerShell ปกติของผู้ใช้ในโฟลเดอร์นี้ โดย repo เป็น Public ตามที่เจ้าของกำหนด:

```powershell
.\scripts\Publish-Repository.ps1
```

สคริปต์ตรวจ repo ว่าเป็น Public และตรวจ origin; หากในเครื่องยังไม่มี commit จะเชื่อมกับ main บน GitHub โดยคงไฟล์ทำงานไว้ จากนั้น stage source/docs/design และ push การเปลี่ยนแปลง.
ไม่เก็บไฟล์เสียง, session ส่วนตัว หรือ build outputs ขึ้น GitHub. ต้องล็อกอิน GitHub CLI ในบัญชีผู้ใช้ก่อน.

