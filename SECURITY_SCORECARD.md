# SECURITY SCORECARD — SecureNotes

> Bảng tự chấm từng mục của đề (Bảo mật ứng dụng web và CSDL). Mỗi dòng trả lời ba câu hỏi mà giảng viên sẽ hỏi: **chặn bằng gì, nằm ở đâu trong code, chứng minh bằng gì.**
> Đi kèm `roadmap.md` (mã ca kiểm thử T01–T25 nằm ở mục 6 của roadmap).

## Cách dùng

1. Cột **Vị trí code**: điền đường dẫn + tên hàm/SP khi làm xong, ví dụ `backend/Services/PasswordHasher.cs:Hash()`. Trong mã nguồn, chỗ đó phải có chú thích dạng `// [Phần 3 #1] ...`.
2. Cột **Bằng chứng**: tên file trong `docs/evidence/`, quy ước `T01_before.png` (bản baseline, tấn công thành công) và `T01_after.png` (bản gia cố, bị chặn). Mục không tấn công được từ bên ngoài thì ghi tên script hoặc ảnh chụp cấu hình.
3. Cột **Trạng thái**: ☐ chưa làm · ◐ đang làm · ☑ xong và có bằng chứng. Chỉ đánh ☑ khi cả ba cột đã điền.
4. Người phụ trách theo mã vai trò A–E ở mục 10 của `roadmap.md`. Họ tên điền ở đó, không lặp lại ở đây.
5. Trước buổi báo cáo (P12): không còn dòng MUST nào ở trạng thái ☐ hoặc ◐.

---

## Phần 1 — Dạng tấn công web (cần ≥ 4, mục tiêu 7/7)

| Mã | Tấn công | Biện pháp chặn | PT | Ca kiểm thử | Vị trí code | Bằng chứng | Trạng thái |
|---|---|---|---|---|---|---|---|
| P1-1 | XSS | React escape mặc định, không dùng `dangerouslySetInnerHTML`, CSP, whitelist `http/https` cho URL nguồn, cookie `HttpOnly` | D | T03, T04, T05 | | | ☐ |
| P1-2 | CSRF | Antiforgery token qua header, `SameSite=Strict`, kiểm tra `Origin`, thao tác ghi chỉ `POST` | E | T06 | | | ☐ |
| P1-3 | SQL Injection | Stored Procedure tham số hóa, `sp_executesql`, tài khoản DB chỉ `EXECUTE` | C | T01, T02 | | | ☐ |
| P1-4 | Tấn công xác thực | PBKDF2, khóa tạm, rate limit (cả bước 2FA), CAPTCHA, 2FA, session mạnh, kiểm tra `state` OAuth | A | T07, T08, T18, T19, T20, T21, T24 | | | ☐ |
| P1-5 | Lỗi logic | Kiểm tra chủ sở hữu mọi truy vấn (IDOR), link chia sẻ có token/hạn/thu hồi, chống HPP, whitelist trường khi đăng ký, xử lý nguyên tử chống race | B | T09, T10, T12, T22 | | | ☐ |
| P1-6 | Trình duyệt và riêng tư | CSP, HSTS, `Referrer-Policy`, `Permissions-Policy`, `frame-ancestors`, `Cache-Control: no-store` cho trang nhạy cảm | E (+D) | T25 | | | ☐ |
| P1-7 | Sửa/xóa log | `audit_logs` chỉ ghi thêm (`DENY UPDATE/DELETE`), chuỗi băm phát hiện sửa log | C | T13 | | | ☐ |

## Phần 2 — Biện pháp bảo mật (cần ≥ 4, mục tiêu 7/7)

| Mã | Lĩnh vực | Việc làm | PT | Ca kiểm thử | Vị trí code | Bằng chứng | Trạng thái |
|---|---|---|---|---|---|---|---|
| P2-1 | Xác thực và trao quyền | Mật khẩu mạnh + băm, khóa tạm, CAPTCHA, 2FA, RBAC, đổi tên tài khoản mặc định, tắt autocomplete trên form | A | T07, T12, T24 | | | ☐ |
| P2-2 | Phiên làm việc | Token 256 bit, không đặt trên URL, timeout, 1 phiên/tài khoản, phạm vi cookie chặt, token CSRF, kết thúc phiên kiểu phản ứng | A | T08 | | | ☐ |
| P2-3 | Máy chủ web | Cấm liệt kê thư mục, chặn `PUT/DELETE/TRACE/MOVE`, xóa trang mặc định, quyền tối thiểu, cập nhật bản vá | E | T17, T23 | | | ☐ |
| P2-4 | CSDL web | Truy vấn tham số hóa/Stored Procedure, phân quyền theo vai trò | C | T01, T02, T15 | | | ☐ |
| P2-5 | Hệ thống file | Upload ngoài web root, không quyền thực thi, bộ lọc tên/đường dẫn, xóa file `.bak/.old`, mã nguồn biên dịch | B (+E) | T11, T23 | | | ☐ |
| P2-6 | Trình duyệt | CSP, `object-src 'none'`, không nhúng plugin hay nội dung ngoài, header bảo mật | E (+D) | T25 | | | ☐ |
| P2-7 | Quản lý log | Phân loại, lưu lâu dài, cấm xóa/sửa, log file xoay vòng không ghi bí mật | C (+E) | T13 | | | ☐ |

## Phần 3 — Lỗ hổng CSDL (cần ≥ 5/10, mục tiêu 10/10)

| Mã | Lỗ hổng | Biện pháp | PT | Ca kiểm thử / cách chứng minh | Vị trí code/cấu hình | Bằng chứng | Trạng thái |
|---|---|---|---|---|---|---|---|
| P3-1 | SQL Injection | Stored Procedure/tham số hóa | C | T01, T02 | | | ☐ |
| P3-2 | Dynamic SQL trong SP | Baseline có SP nối chuỗi; bản sửa dùng `sp_executesql` có tham số | C | T02 | | | ☐ |
| P3-3 | Dùng chung tài khoản đặc quyền | Tài khoản `notes_app` chỉ `EXECUTE`; vô hiệu hóa/đổi tên `sa` | C | T15 | | | ☐ |
| P3-4 | Lạm dụng đặc quyền truy cập trực tiếp | `DENY SELECT/INSERT/UPDATE/DELETE` trên bảng gốc; admin không có endpoint đọc nội dung | C | T15, T12 | | | ☐ |
| P3-5 | Mật khẩu CSDL yếu/mặc định | ≥ 10 ký tự đủ 4 loại; `CHECK_POLICY`/`CHECK_EXPIRATION` nếu nền tảng hỗ trợ | E | Không tấn công từ ngoài: ảnh chụp cấu hình + script kiểm tra | | | ☐ |
| P3-6 | Lộ chuỗi kết nối | Biến môi trường/Docker secrets hoặc Data Protection API; không commit vào git | E | `gitleaks` sạch (cả lịch sử git) + ảnh chụp cấu hình | | | ☐ |
| P3-7 | Dữ liệu nhạy cảm dạng rõ | Băm mật khẩu, AES-256-GCM cho ghi chú/TOTP secret (TDE: nếu kịp) | C | T14 | | | ☐ |
| P3-8 | Truyền CSDL không mã hóa | TLS giữa API và SQL Server (`Encrypt=True`, `TrustServerCertificate=False`) | E | T16 (bắt gói tin trước/sau) | | | ☐ |
| P3-9 | Lỗi cấu hình HQTCSDL | Cập nhật bản vá, tắt tính năng thừa, chỉ lắng nghe mạng nội bộ, quyền thư mục dữ liệu | E | `nmap` từ ngoài không thấy 1433 + ảnh chụp cấu hình | | | ☐ |
| P3-10 | Thiếu audit | Bảng `audit_logs` + SQL Server Audit/Extended Events | C | T13 + ảnh chụp cấu hình Audit | | | ☐ |

---

## Điểm cộng ngoài đề

| Mã | Nội dung | PT | Ca kiểm thử | Vị trí code | Bằng chứng | Trạng thái |
|---|---|---|---|---|---|---|
| X-1 | Đăng nhập Google (OIDC + PKCE), khóa theo `sub`, không tự liên kết khi trùng email, không lưu token Google | A | T18–T21 | `backend/SecureNotes.Api/Security/GoogleOidcEvents.cs`, `database/005_p5.sql` | OAuth entry/return URL and CSRF-link smoke checks; dummy-config test for `state`, `nonce`, PKCE S256 and invalid-state callback; live Google cases pending | ◐ |
| X-2 | Chia sẻ link: token 256 bit chỉ lưu mã băm, hạn dùng, thu hồi, 404 chung cho mọi lỗi | B | T10 | | | ☐ |
| X-3 | Đính kèm file/ảnh (ảnh: `png`, `jpg`, `webp`; văn bản: `pdf`, `docx`, `txt`, `md`): allowlist, magic bytes, kiểm tra cấu trúc ZIP và macro (`docx`), UTF-8/không NUL (`txt`/`md`), mã hóa lại ảnh, văn bản chỉ tải xuống (`attachment` + `nosniff`), tên ngẫu nhiên, ngoài web root | B | T11 | | | ☐ |
| X-4 | 2FA TOTP: chặn dò mã và dùng lại mã | A | T24 | | | ☐ |
| X-5 | Công tắc tắt nhanh tải lên và chia sẻ link | B | Chạy thử bật/tắt | | | ☐ |
| X-6 | ID công khai dạng GUID, không dùng số tuần tự | B | T09 | | | ☐ |

---

## Tổng hợp (cập nhật trước khi nộp)

| Phần | Mục tiêu | Đã xong (☑) | Còn thiếu |
|---|---|---|---|
| Phần 1 — Tấn công web | 7/7 (tối thiểu 4) | _ / 7 | |
| Phần 2 — Biện pháp | 7/7 (tối thiểu 4) | _ / 7 | |
| Phần 3 — Lỗ hổng CSDL | 10/10 (tối thiểu 5) | _ / 10 | |
| Điểm cộng | | _ / 6 | |

---

## Cách chứng minh các mục không tấn công được từ bên ngoài

Giảng viên white-box chấm các mục này bằng cách đọc cấu hình, nên cần chuẩn bị sẵn:

| Mục | Chuẩn bị |
|---|---|
| P3-3, P3-4 | Script `db/verify_permissions.sql`: đăng nhập `notes_app`, thử `SELECT` bảng gốc (bị từ chối) và gọi SP (chạy được) |
| P3-5 | Ảnh chụp `CREATE LOGIN ... CHECK_POLICY` và trạng thái login `sa` bị vô hiệu hóa/đổi tên |
| P3-6 | Kết quả `gitleaks detect` sạch; ảnh chụp cấu hình biến môi trường/secrets (đã che giá trị) |
| P3-8 | Ảnh bắt gói tin API↔DB trước và sau khi bật TLS |
| P3-9 | Kết quả `nmap` từ máy ngoài; ảnh chụp cấu hình lắng nghe của SQL Server |
| P3-10 | Ảnh chụp bảng `audit_logs` có dòng sự kiện; cấu hình SQL Server Audit |

---

## Hạn chế đã biết (trả lời trung thực khi giảng viên hỏi)

Sửa danh sách này theo thực tế khi nộp; mục nào đã khắc phục thì xóa.

- [ ] Chưa chống tấn công từ chối dịch vụ (DoS) quy mô lớn; hệ thống chạy trên một VPS duy nhất.
- [ ] Tài liệu `pdf`/`docx` không được làm sạch nội dung như ảnh; chỉ kiểm tra cấu trúc, cho tải xuống, và quét ClamAV nếu đã làm.
- [ ] Chưa có quên mật khẩu và xác minh email (đã tạm bỏ tính năng gửi mail).
- [ ] Mã hóa dữ liệu bảo vệ khi DB/backup bị lộ, **không** chống được kẻ chiếm hoàn toàn máy chủ ứng dụng (khóa KEK nằm trong bộ nhớ của nó).
- [ ] TDE: ghi "đã làm" hoặc "chưa làm" tùy thực tế.
- [ ] Chuỗi băm cho log: ghi "đã làm" hoặc "chưa làm" tùy thực tế.
- [ ] Đăng nhập Google chạy ở chế độ Testing, chỉ các Gmail trong danh sách test user mới dùng được.
- [ ] Bản baseline dễ tổn thương chỉ chạy local, tag `v0-vulnerable`, không có trên môi trường kiểm thử.
