# XSurLab

โปรแกรมมิกซ์เสียงรอบทิศทางบน Windows · WPF / .NET 10 · รุ่น 0.5

## เปิดแอป

ไฟล์ที่แพ็กแล้ว (`XSurLab.exe` ไฟล์เดียว ไม่ต้องติดตั้ง .NET) สร้างได้ด้วย

```powershell
.\scripts\Publish-App.ps1            # artifacts/XSurLab-win-x64/XSurLab.exe
.\scripts\Publish-App.ps1 -Runtime win-arm64
```

CI แนบไฟล์นี้ไว้เป็น artifact `XSurLab-win-x64` ทุกครั้งที่ build ผ่าน.
ถ้าจะรันจากซอร์ส ต้องมี .NET 10 SDK:

```powershell
dotnet run --project src/XSurLab.App
```

เปิดไฟล์ได้จาก command line หรือลากมาวาง: `XSurLab.exe mix.xsurlab.json` หรือ `XSurLab.exe a.wav b.wav`.

## ใช้งานได้ในรุ่นนี้

**นำเข้าสื่อ**
- WAV PCM16/24/32 และ float32 แบบ mono, stereo และ multichannel (bed สูงสุด 18 ช่องตาม WAVE channel mask)
- WAV ธรรมดาไม่เกิน 6 ช่องใช้ mask มาตรฐาน; ไฟล์ 7 ช่องขึ้นไปที่ไม่มี mask จะถูกปฏิเสธเพราะลำดับช่องกำกวม
- ไฟล์ที่ data chunk เกิน 8 MiB อ่านสตรีมจากดิสก์ทีละบล็อก ไม่โหลดทั้งไฟล์; ไฟล์เล็กที่ถอดรหัสแล้วรวมกันไม่เกิน 1 GiB
- Media ต่างอัตรา sample แปลงด้วย Kaiser-windowed sinc (วัดได้ −101 dB ที่ 1 kHz 44.1→48 kHz)

**มิกซ์และตำแหน่งเสียง**
- สูงสุด 32 แทร็ก; gain, mute, solo, start offset, กลุ่ม (stem) และ LFE send แยก (low-pass 120 Hz)
- Layout: Stereo 2.0, 5.1 Side, 5.1 Back, 7.1, 5.1.4 และ 7.1.4 (ลำโพงเพดาน 4 ตัวที่ 45°)
- Panner สองชั้น: azimuth, elevation (0–90°) และ spread (0–360°) รักษากำลังเสียงรวม; bed ส่งตรงตามช่องของ mask
- EQ 6 แบนด์ต่อแทร็ก: high-pass, low shelf, peak 2 ตัว, high shelf, low-pass พร้อมกราฟลากปรับได้
- ปรับทุกค่าระหว่างเล่นได้ และค่อยๆ ไล่ค่าใน 20 ms จึงไม่มีเสียง click

**Timeline และการแก้ไข**
- ทุกแทร็กเป็น clip บนแกนเวลาเดียว พร้อม waveform; คลิก ruler เพื่อวาง playhead, ลาก clip เพื่อเลื่อนจุดเริ่ม (snap ที่ 0, playhead และขอบ clip; กด Shift เพื่อเลื่อนอิสระ)
- เล่นจาก playhead, seek ระหว่างเล่นได้ทันที, หยุดแล้ว playhead อยู่ตรงที่ได้ยินล่าสุด
- Undo/Redo สูงสุด 200 ขั้น; การลาก slider ต่อเนื่องนับเป็นขั้นเดียว

**ฟังผ่านลำโพง (WASAPI)**
- **Shared**: ผ่าน mixer, ระดับเสียง และเอฟเฟกต์ของ Windows; buffer 50 ms
- **Exclusive**: ส่ง sample ของ session ให้ driver ตรงๆ ที่อัตรา session (float32 หรือ PCM 24-in-32 / 32 / 24 / 16 ตามที่ driver รับ); period ประมาณ 10 ms; ระดับเสียง Windows ไม่มีผลและแอปอื่นจะเงียบระหว่างเล่น
- ทั้งสองโหมดทำงานแบบ event-driven ตาม period ของอุปกรณ์
- รายชื่ออุปกรณ์อัปเดตเองเมื่อเสียบ/ถอด/เปิด/ปิดอุปกรณ์ เปลี่ยน default หรือเปลี่ยน format ใน Windows; ถ้าอุปกรณ์ที่ใช้อยู่หายไป จะหยุดเล่นและแจ้งในแถบสถานะ (ไม่เล่นต่อที่อุปกรณ์อื่นเอง)
- Stereo fold-down (Lo/Ro) สำหรับฟังเทียบ หรือปิดเพื่อส่งช่อง discrete
- Meter แยกช่องพร้อม peak hold, LUFS momentary/short-term/integrated และ true peak สด

**ปรับเทียบลำโพง (Calibrate…)**
- ค่าแยกตามอุปกรณ์และ layout (stereo fold-down มีค่าของตัวเอง) เก็บใน `%APPDATA%\XSurLab\calibration.json`; ไฟล์ก่อนหน้าเก็บไว้เป็น `.bak`
  และไฟล์ที่อ่านไม่ได้จะถูกคัดลอกเป็น `calibration.json.unreadable-<เวลา>` ก่อนบันทึกทับครั้งแรก
- เสียงทดสอบ pink noise −20 dBFS RMS ทีละลำโพง: ลำโพงหลัก 500 Hz–2 kHz, sub 20–120 Hz, peak ไม่เกิน −8 dBFS;
  กด Test ทีละตัว หรือ Cycle ไล่ตามเข็มนาฬิกาตัวละ 3 วินาที (sub ท้ายสุด) แล้ววัดด้วย SPL meter ที่จุดนั่งฟัง
- Trim −20…+10 dB และ delay 0–50 ms ต่อลำโพง ปรับระหว่างเสียงทดสอบดังอยู่ได้; ใส่ระยะห่าง (เมตร) แล้วให้คำนวณ delay ได้
- Bass management: เลือก **Small/Large** แยกต่อลำโพงหลักและลำโพงเพดาน; Small ใช้ crossover Linkwitz–Riley 24 dB/oct ที่ 60/80/100/120 Hz ส่งเบสไป sub รวมกับ LFE (+10 dB เลือกได้); Large เก็บเสียงเต็มย่าน ไม่ส่งเบสซ้ำไป sub
- ค่าเริ่มต้นและ calibration เดิมเป็น Small ทุกตัว; การสลับขนาดไล่ค่าใน 20 ms ขณะเล่น เสียงทดสอบข้าม bass management เสมอ
- Calibration อ่าน schema v1/v2 และบันทึกเป็น v2 พร้อม `.bak` ของไฟล์เดิม; แอปรุ่นเก่าอ่าน v2 ไม่ได้ ต้องใช้รุ่นใหม่นี้ต่อหรือคืนไฟล์ v1 จาก backup
- ระดับ sub: เปิด bass management และ LFE +10 dB → ตั้ง sub ให้อ่านได้เท่าลำโพงหลัก; ปิด bass management → ตั้ง sub ให้ดังกว่าลำโพงหลัก 10 dB
  (มิกซ์คาดว่า LFE ดังกว่า 10 dB ในย่านของมัน)
- ใช้กับเสียงฟังเท่านั้น ไม่เข้า export/deliver; สวิตช์ **Apply** สลับ calibrate/flat ระหว่างเล่นได้ (ไล่ค่าใน 20 ms)

**ส่งออกและส่งมอบ**
- Export WAV แบบ PCM 24/16-bit (TPDF dither) หรือ float 32-bit พร้อม BWF `bext` ที่บันทึกค่า loudness
- ไฟล์เกิน 4 GiB เขียนเป็น RF64 อัตโนมัติ
- **Deliver…** render mix, ไฟล์ mono แยกลำโพง (multi-mono สำหรับ encoder) และ stem ตามกลุ่มในรอบเดียว ยาวเท่ากันทุกไฟล์
- รายงาน QC (JSON + ข้อความ) ตรวจ sample peak, integrated loudness และ true peak เทียบ EBU R128 (−23 LUFS), ATSC A/85 (−24 LKFS) หรือ streaming (−14 LUFS) พร้อม SHA-256 ของทุกไฟล์
- วัด loudness ตาม ITU-R BS.1770-4, loudness range ตาม EBU Tech 3342 และ true peak แบบ oversample

**Session และความปลอดภัยของไฟล์**
- บันทึก/เปิด `*.xsurlab.json` (schema v2; ไฟล์ v1 เปิดได้) อ้าง media ด้วย relative path ไม่คัดลอกไฟล์เสียง
- เขียนไฟล์ผ่านไฟล์ชั่วคราวแล้วค่อยแทนที่; ยกเลิก, clipping หรือ error จะไม่ทับไฟล์เดิม และไม่แก้ไฟล์ต้นฉบับ

## เริ่มใช้งาน

1. **+ Import WAV** (หรือลากไฟล์มาวาง) → เลือก **Speaker layout** และ **Sample rate**
2. เลือกแทร็ก → ลากในวงกลม panner, ปรับ gain/EQ ใน Channel strip และแท็บ Track EQ
3. เลือกอุปกรณ์ใน **WASAPI OUTPUT** และ **MODE** → **▶ Play monitor** (หรือกด Space)
4. ครั้งแรกกับลำโพงชุดใหม่: **Calibrate…** → ตั้ง MONITOR ให้เบาก่อน → Test ทีละลำโพง → ปรับ trim/delay → **Save calibration**
5. **Export WAV →** สำหรับไฟล์เดียว หรือ **Deliver…** สำหรับชุดส่งมอบพร้อมรายงาน QC

Master เริ่มที่ −6 dB เพื่อเผื่อ headroom. Monitor เริ่มที่ 50% และแยกจาก master/export.
ใน Exclusive ระดับเสียง Windows ไม่ช่วยลดเสียง — ตั้ง MONITOR ก่อนกดเล่น.
การเปลี่ยน layout, sample rate, start offset, อุปกรณ์, โหมด หรือ fold-down จะหยุดการเล่น.

| คีย์ | ทำอะไร |
| --- | --- |
| Space | เล่น/หยุดจาก playhead |
| Esc | หยุด หรือยกเลิกงานที่กำลังทำ |
| ← → Home End | เลื่อน playhead (เมื่อคลิกที่ timeline แล้ว) |
| Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z | Undo / Redo |
| Ctrl+N / O / S / I | New / Open / Save / Import |
| Ctrl+E / Ctrl+D | Export / Deliver |

## ตรวจ build และเสียง

```powershell
dotnet build XSurLab.slnx -c Release
.\scripts\Verify.ps1
# เล่นเสียงเงียบบนทุกอุปกรณ์ที่ต่ออยู่ ทั้ง Shared และ Exclusive (drain, seek, cancel)
.\scripts\Verify.ps1 -Devices
```

ชุดทดสอบเป็น console harness ที่คืน exit code เมื่อไม่ผ่าน และลบไฟล์ทดสอบของตัวเองเมื่อจบ.
บรรทัด `GAP` คือสิ่งที่เครื่องนั้นตรวจไม่ได้ (เช่นอุปกรณ์ไม่รับ layout นั้น) ไม่ใช่ความล้มเหลว.
ระหว่าง `-Devices` ช่วง Exclusive เสียงจากแอปอื่นจะเงียบไปครู่หนึ่ง.
CI บน Windows รัน build, Verify.ps1 และแพ็ก exe; การตรวจอุปกรณ์จริงทำเฉพาะเครื่องที่มี audio endpoint.
ไม่มี library จาก NuGet ภายนอก (การแพ็ก self-contained ใช้ runtime pack ของ .NET เอง).

## ขอบเขต

ยังไม่มี: ASIO, การอัด/จับเสียงระบบ, automation, การตัด/แบ่ง clip, dynamics (compressor/limiter),
การวัดห้องด้วยไมค์/room EQ, binaural/HRTF, object audio, DTS encoding หรือ DTS:X authoring.
Workspace Routing / Up-Downmix / Monitor ในต้นแบบยังเป็นแนวคิด — ต้นแบบ 8 หน้าอยู่ที่
[design/xsurlab-studio.html](design/xsurlab-studio.html).

- อัตรา session 44.1 / 48 / 96 kHz; ความยาว render สูงสุด 1 ชั่วโมง
- Shared + discrete surround ต้องมี endpoint ที่ Windows ตั้งจำนวนช่องและ channel mask ตรงกับ session
  (5.1 Side 0x060F, 5.1 Back 0x003F, 7.1 0x063F, 5.1.4 0x2D60F, 7.1.4 0x2D63F) และรองรับอัตรา session;
  โปรแกรมไม่รับ format ใกล้เคียง ไม่ downmix เอง และไม่สลับ side/back
- Exclusive ใช้ได้เมื่อ driver รับ format นั้นตรงตัว — อาจรับ layout ที่ Windows ไม่ได้ตั้งไว้ (เช่น 7.1 บนการ์ดที่ตั้งเป็น 5.1)
  และต้องเปิด "Allow applications to take exclusive control" ใน Windows
- Buffer/period ที่แสดงไม่ใช่ round-trip latency; ยังไม่รับรองงาน low-latency
- Monitor หยุดเมื่อค่าหลัง monitor gain เกิน 0 dBFS; ไม่มี limiter หรือ normalization อัตโนมัติ
- Stereo fold-down เป็น Lo/Ro เพื่อฟังเทียบ: C, surround และเพดานลงข้างที่เกี่ยวข้องที่ −3 dB และไม่รวม LFE
- Panner เป็น amplitude panner ไม่จำลองระยะทาง ห้อง หรือ HRTF
- Calibrate ไม่ได้ฟังเสียงจากไมค์เอง: ระดับเสียงต้องอ่านจาก SPL meter และระยะต้องวัดเอง; Shared ผ่านเอฟเฟกต์ของ Windows
  จึงควรปิด room correction/virtual surround ระหว่าง calibrate
- Bass management ตัดเสียงทุ้มเฉพาะลำโพง Small เมื่อเปิดใช้งาน และไม่ low-pass ช่อง LFE ซ้ำ;
  trim บวก หรือ LFE +10 dB อาจทำให้ monitor เกิน 0 dBFS แล้วหยุดเล่น — ลด MONITOR ลง
- เมื่อเพลงจบ ลำโพงที่ตั้ง delay จะเล่นต่อจนครบ delay; playhead ไม่นับ delay (ไม่เกิน 50 ms)
- รายงาน QC เทียบเป้าที่เลือก ไม่ใช่การรับรอง DTS หรือมาตรฐานส่งมอบภาพยนตร์

ดู [โครงสร้าง, ลำดับช่อง และเส้นทางสัญญาณ](docs/architecture.md).

## สคริปต์

- `scripts/Verify.ps1` — ชุดทดสอบ (เพิ่ม `-Devices` เพื่อตรวจกับอุปกรณ์จริง)
- `scripts/Publish-App.ps1` — แพ็ก `XSurLab.exe` แบบ self-contained ไฟล์เดียว
- `scripts/Publish-Repository.ps1` — ผูก repo ในเครื่องกับ `xjanova/XSurLab` (Public) แล้ว push จาก PowerShell ปกติ;
  ไม่เก็บไฟล์เสียง, session ส่วนตัว หรือ build output ขึ้น GitHub
