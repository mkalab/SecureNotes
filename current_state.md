# Trạng thái hiện tại — SecureNotes

**Cập nhật:** 2026-10-29
**Giai đoạn hiện tại:** P8 — Quản trị và kiểm soát truy cập
**Trạng thái:** P0–P8 đã triển khai và kiểm tra tích hợp. P5 chưa được xác minh đăng nhập end-to-end với Google vì Google Cloud OAuth client/test users chưa được cấu hình. Việc sao lưu KEK và công cụ xoay khóa tự động vẫn là việc vận hành trước production.

## P0 — Chuẩn bị: hoàn thành

- Khởi tạo cấu trúc repository, `.gitignore`, README, CI build/lint và cấu hình gitleaks cho hook/CI.
- Đặt backend ở .NET 10 (`net10.0`); SDK đã dùng để xác minh: 10.0.113.
- Khai báo Node.js `>=22.12.0 <23`; môi trường đã dùng Node.js 22.
- Thêm API health, ứng dụng React/Vite, OpenAPI, Dockerfile và Docker Compose.
- SQL Server healthy, frontend chạy tại `http://localhost:5173`, health endpoint qua proxy hoạt động.
- Hợp đồng API ban đầu do trợ lý tự phác thảo từ roadmap, không phải hợp đồng được người dùng cung cấp. Hiện `docs/openapi.yaml` được đồng bộ với các endpoint đã triển khai đến P8.

## P1 — CSDL nền tảng và phân quyền: hoàn thành

- Có schema `app`, các bảng users, categories, notes, note items và audit logs; stored procedures cho user và audit.
- Tách tài khoản `notes_migrator` và `notes_app`; `notes_app` chỉ được thực thi stored procedures và bị từ chối truy cập trực tiếp schema `dbo`.
- Mật khẩu DB local được giữ trong `.env` quyền `600`; login `sa` đã bị vô hiệu hóa.
- Bootstrap kiểm tra quyền trực tiếp và quyền gọi stored procedure, đồng thời chạy lặp lại được.

## P2 — Lõi ứng dụng: chức năng đã triển khai

- Đăng ký/đăng nhập email-mật khẩu; mật khẩu được băm PBKDF2-HMAC-SHA256 với salt riêng.
- Cookie đăng nhập HttpOnly, SameSite=Strict; mọi POST `/api` đều cần antiforgery token.
- API đăng ký, đăng nhập, đăng xuất, current account; CRUD ghi chú, danh mục và mục checklist; ghi chú con; lọc danh mục, tìm kiếm.
- Stored procedures thực hiện truy vấn tham số hóa và kiểm tra chủ sở hữu; API không nhận `UserId` từ request làm nguồn quyền.
- Giao diện React/CSS responsive: lưới ghi chú, tạo/sửa/xóa, kéo thả sắp xếp, ghim, màu, mức ưu tiên, URL nguồn HTTP/HTTPS, danh mục và ghi chú con/checklist.
- Nội dung ghi chú render như text; URL nguồn chỉ tạo liên kết HTTP/HTTPS với `rel="noopener noreferrer"`.
- Thêm migration `database/002_p2.sql` và cập nhật OpenAPI/README.
- Theo phạm vi đã thống nhất, không tạo baseline cố ý dễ tổn thương, mã SQLi/XSS/CSRF/IDOR yếu, tag `v0-vulnerable` hay bằng chứng khai thác “trước”. Phần này được bỏ qua; các chức năng P2 còn lại đã triển khai.
- Thêm `scripts/test-p2-smoke.sh` để kiểm tra các luồng P2 và tự dọn tài khoản/dữ liệu thử nghiệm.

## P3 — Gia cố xác thực, phiên và ứng dụng web: hoàn thành MUST

- Duy trì PBKDF2-HMAC-SHA256 600.000 vòng, thêm chính sách đăng ký mật khẩu (ít nhất 12 ký tự, chữ hoa/thường, số và ký tự đặc biệt) ở frontend và server; tắt autocomplete trên trường mật khẩu.
- Thêm khóa tài khoản 15 phút sau 5 lần đăng nhập sai và rate limit endpoint đăng ký/đăng nhập theo IP; lỗi thông tin đăng nhập và khóa tài khoản dùng cùng thông báo với thời gian phản hồi thất bại tối thiểu 500 ms.
- Tạo session ID 256 bit bằng `RandomNumberGenerator`; chỉ lưu SHA-256 hash trong `dbo.user_sessions`; mỗi tài khoản có tối đa một session, timeout idle 20 phút và tuyệt đối 8 giờ. Đăng nhập cấp session mới; đăng xuất thu hồi ở server.
- Cookie production dùng `__Host-`, HttpOnly, Secure, SameSite=Strict, Path=/ và không Domain; môi trường Development dùng cookie không Secure do frontend/API local chạy HTTP.
- Bắt buộc Origin allowlist và antiforgery `X-CSRF-TOKEN` cho mọi phương thức thay đổi dữ liệu; React tiếp tục lấy token từ `/api/csrf` cho mọi POST. Sau 5 lỗi CSRF/Origin hoặc truy cập tài nguyên không thuộc quyền trong 5 phút, session bị thu hồi và sự kiện được ghi vào audit log bất biến.
- Thêm CSP, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`, HSTS ngoài Development, `Cache-Control: no-store` cho API; loại banner Server ở Kestrel.
- API giới hạn request body 1 MiB; lỗi production trả thông báo chung; ghi chú được render dạng text, URL nguồn chỉ chấp nhận HTTP/HTTPS, ID công khai tiếp tục là GUID ngẫu nhiên.
- Thêm migration `database/003_p3.sql` với stored procedures tham số hóa cho session/lockout; quyền sở hữu vẫn được lấy từ principal đã xác thực. Audit log được giữ lại và UserId chuyển NULL khi xóa tài khoản.
- CAPTCHA là SHOULD chưa cấu hình dịch vụ; 2FA TOTP là SHOULD được hoãn sau P4 như phụ thuộc trong roadmap. Không tạo baseline/tag/bằng chứng khai thác “trước” theo phạm vi đã thống nhất.

## P4 — Mã hóa dữ liệu: MUST cho ghi chú đã triển khai

- `CryptoService` dùng AES-256-GCM, nonce 12 byte và tag 16 byte; ciphertext lưu theo định dạng `nonce|tag|ciphertext`, xác thực AAD theo user/note/field để ngăn tráo ciphertext giữa bản ghi hoặc trường.
- Mỗi tài khoản có DEK ngẫu nhiên 32 byte; DEK được bọc bằng KEK 32 byte từ cấu hình `SecureNotes:KeyEncryptionKey` (`SECURENOTES_KEK` trong Compose). DEK chỉ được giải mã trong bộ nhớ để đọc/ghi và được xóa khỏi buffer sau sử dụng.
- API mã hóa tiêu đề/nội dung ghi chú và tiêu đề checklist; dữ liệu legacy được chuyển đổi tự động lúc API khởi động dưới DB application lock, sau đó xóa plaintext ở cột cũ. Stored procedures từ chối các bản ghi mới thiếu ciphertext.
- Tìm kiếm lọc sau khi giải mã các ghi chú thuộc user hiện tại. Các endpoint CRUD hiện có vẫn được giữ nguyên; frontend không cần thay đổi hợp đồng API.
- `scripts/init-dev-env.sh` tạo KEK local trong `.env` quyền `600`, file này bị Git ignore. README ghi hướng dẫn giữ nguyên KEK, backup tách biệt và các bước xoay khóa.
- Chưa có TOTP secret vì chức năng 2FA chưa được triển khai. Không tạo endpoint test-only hoặc endpoint roadmap chưa có.

## P5 — Đăng nhập Google OIDC: code đã triển khai

- Thêm Google OIDC dùng Authorization Code + PKCE, chỉ scope `openid email profile`; handler ASP.NET xác minh state/nonce/chữ ký/issuer/audience/thời hạn và không lưu token Google.
- Callback chỉ chấp nhận `sub`, email hợp lệ và `email_verified=true`. `dbo.external_logins` lưu provider + subject duy nhất; user mới có DEK P4 và không có password hash.
- Không tự liên kết email trùng. Cài đặt liên kết yêu cầu password đúng của tài khoản hiện tại, session còn hiệu lực khi callback hoàn tất, Origin và CSRF; OIDC state lưu hash session chứ không đưa session token thô vào URL. SQL transaction đảm bảo một Google identity/account không thể liên kết trùng.
- Callback/FrontendOrigin cấu hình cố định; `returnUrl` chỉ chấp nhận path local. Sau login/link tạo app session mới và ghi `google_login`/`google_account_link` vào audit log.
- Forwarded headers được xử lý trước middleware; API chỉ tin `X-Forwarded-For/Proto/Host` từ IP `ForwardedHeaders:KnownProxies` cấu hình rõ (tối đa một hop). Môi trường Compose không khai báo proxy tin cậy.
- UI đăng nhập có nút Google; khu vực Cài đặt liên kết yêu cầu nhập lại mật khẩu. OIDC handler chỉ được đăng ký khi đủ cấu hình hợp lệ; nếu chưa cấu hình, endpoint quay về UI với thông báo phù hợp và đăng nhập email/mật khẩu vẫn hoạt động.
- Chưa có Google Cloud project, OAuth client, Gmail test users hoặc thông tin Client ID/Secret trong môi trường này. Vì vậy chưa thể chứng minh login thật và các ca T18–T21 hoàn chỉnh; không có nhánh đăng nhập giả lập/test-only.

## P6 — Tệp đính kèm ghi chú: hoàn thành

- Hỗ trợ PDF, TXT, MD, PNG, JPG/JPEG. Server đối chiếu phần mở rộng và MIME; văn bản phải là UTF-8 hợp lệ, PDF được phân tích cấu trúc, ảnh được giải mã/chuẩn hóa và bỏ metadata trước khi lưu.
- Mặc định tối đa 10 MiB/tệp, 20 tệp/ghi chú và 100 MiB/tài khoản. Stored procedure khóa hàng tài khoản khi đặt chỗ, tính cả các upload đang chờ để tránh vượt quota do upload song song.
- Nội dung được mã hóa AES-256-GCM với DEK tài khoản; AAD gắn user/note/attachment ID. File ciphertext dùng tên ngẫu nhiên, nằm ngoài web root, quyền Unix chỉ cho chủ sở hữu đọc/ghi.
- API yêu cầu đăng nhập; ownership được xác minh trong truy vấn/procedure. Upload/xóa dùng CSRF hiện có, upload có rate limit; tải xuống dùng `Content-Disposition: attachment`, `nosniff` và `no-store`. Các thao tác ghi audit log.
- Có nút tải lên/tải xuống/xóa trong phần ghi chú; xóa ghi chú cũng xóa metadata/file liên quan. Background cleanup dọn reservation hết hạn và file mồ côi.
- Compose lưu file trong volume `attachments-data`, chạy `database/006_p6.sql` cùng bootstrap DB và bật tính năng theo mặc định. Có kill switch `SECURENOTES_ATTACHMENTS_ENABLED`; README mô tả giới hạn, storage và vòng đời volume.
- Đã thêm ba endpoint attachment vào `docs/openapi.yaml`; Swagger runtime trả tài liệu hợp lệ sau khi bỏ `[FromForm]` không tương thích khỏi tham số `IFormFile`.
- `./scripts/test-p6-smoke.sh` — đạt: upload TXT/MD/PDF/PNG/JPEG, round-trip download, ciphertext trên volume và xóa file, chặn truy cập chéo tài khoản, MIME/phần mở rộng sai và file vượt 10 MiB.
- `./scripts/test-p6-quota-smoke.sh` — đạt: với giới hạn thử nghiệm một tệp/ghi chú, hai upload đồng thời trả đúng một HTTP 201 và một HTTP 409; script khôi phục giới hạn từ `.env`.
- Chạy lại migration P6 và kiểm tra quyền DB thành công; `notes_app` không thể đọc trực tiếp `dbo.note_attachments` nhưng thực thi được stored procedure.

## P7 — Chia sẻ ghi chú qua link: hoàn thành

- Tạo bearer token từ 32 byte ngẫu nhiên base64url; chỉ trả token một lần lúc tạo và chỉ lưu SHA-256 hash trong `dbo.share_links`.
- Quyền `view`/`edit` và thời hạn được lưu trong DB; mặc định 24 giờ, giới hạn tối đa 30 ngày, có thể thu hồi. Mọi lần truy cập dùng stored procedure kiểm tra link còn hạn, chưa thu hồi, ghi chú còn tồn tại và chủ sở hữu chưa bị vô hiệu hóa.
- Token sai, hết hạn, thu hồi hoặc quyền không đủ đều trả 404. Quyền luôn lấy từ DB; query parameter không thể nâng quyền. Các endpoint public bị rate limit theo IP, API có `no-store`, frontend có `Referrer-Policy: no-referrer`.
- Trang chia sẻ chỉ render text và cho tải tệp của đúng ghi chú. Link `edit` yêu cầu session đăng nhập và chỉ sửa title/content; không cung cấp thao tác file hay thay đổi metadata ghi chú.
- Tạo/thu hồi/truy cập share được ghi vào `audit_logs`; có kill switch `SECURENOTES_SHARING_ENABLED`.
- `./scripts/test-p7-smoke.sh` — đạt: token hash, CSRF, quyền từ DB, query parameters trùng/thao túng, file thuộc đúng note, login cho edit, không đổi metadata, thu hồi/hết hạn cùng 404, audit và rate limit.
- `notes_app` không đọc trực tiếp `dbo.share_links`; chỉ gọi stored procedures đã cấp quyền. Migration `database/007_p7.sql` được tích hợp vào luồng Compose và SQL Server local.

## P8 — Quản trị và kiểm soát truy cập: hoàn thành

- Tài khoản mới luôn có vai trò `user`; `scripts/grant-admin.sh` chỉ cấp admin cho email được chỉ định nếu tài khoản đã bật TOTP, sau đó thu hồi các phiên cũ. Tài khoản admin bắt buộc giữ 2FA.
- Thêm TOTP, mã dự phòng dùng một lần, challenge đăng nhập giới hạn thời gian và chống dùng lại mã TOTP. Secret được mã hóa bằng DEK; chỉ hash mã dự phòng được lưu trong DB.
- Vai trò và trạng thái tài khoản được kiểm tra lại từ DB trên mỗi request đã xác thực. API quản trị yêu cầu role admin cùng phiên đã xác minh 2FA.
- Admin có thể xem metadata tài khoản, khóa/mở khóa, thu hồi phiên, xem audit và thu hồi link chia sẻ; không có API quản trị để đọc nội dung ghi chú hay tệp.
- Migration `database/008_p8.sql` thêm thủ tục quản trị và quyền từ chối UPDATE/DELETE audit log cho `notes_app`; Compose và `scripts/init-local-db.sh` đều áp dụng migration này.
- Audit ghi các sự kiện xác thực, 2FA, quản trị và hoạt động liên quan; Serilog ghi console cùng file xoay theo ngày, tối đa 10 MiB/file và giữ 14 file. Compose lưu log trong volume `api-logs`.
- README và `docs/openapi.yaml` được cập nhật cho P8; `scripts/test-p8-smoke.sh` kiểm tra luồng MFA, recovery code, RBAC, quản trị phiên/tài khoản/share, audit và quyền DB.

## Xác minh P0–P8

- `dotnet build backend/SecureNotes.Api/SecureNotes.Api.csproj --no-restore` — thành công, 0 lỗi; vẫn có 2 advisory dependency bắc cầu được ghi dưới đây.
- `cd frontend && npm run build` — thành công.
- `cd frontend && npm run lint` — hoàn tất; còn một cảnh báo `react(set-state-in-effect)` ở bước tải phiên đăng nhập.
- `docker compose up -d --build` — build thành công; DB healthy, API và frontend đang chạy.
- Bootstrap DB chạy lặp lại thành công; kiểm tra quyền xác nhận `notes_app` không đọc trực tiếp bảng nhưng gọi được stored procedures.
- Smoke test HTTP: đăng ký/đăng nhập, tạo/cập nhật/xóa ghi chú, danh mục và checklist; tìm kiếm; tạo ghi chú con; xác nhận tài khoản khác nhận 404 khi truy cập ghi chú không thuộc mình.
- POST thiếu CSRF token bị từ chối; URL nguồn ngoài HTTP/HTTPS bị từ chối.
- Frontend trả HTTP 200; `/api/health` trả `ok`; `/api/health/ready` trả `ready`; `/api/auth/me` trả 204 khi chưa đăng nhập.
- Giao diện đã mở và đăng nhập thử trong browser; tài khoản và ghi chú tạm dùng cho smoke test đã được dọn khỏi DB.
- OpenAPI YAML, Compose config, shell syntax và `git diff --check` đều hợp lệ.
- Tại mốc P6, hợp đồng OpenAPI có 22 path; Swagger runtime sinh tài liệu hợp lệ và có đủ ba path attachment.
- `./scripts/test-p2-smoke.sh` — tất cả kiểm tra đăng ký/đăng nhập, CSRF, CRUD, tìm kiếm, ghi chú con, URL an toàn và phân quyền giữa hai tài khoản đều đạt; dữ liệu test được dọn sau khi chạy.
- `./scripts/test-p2-smoke.sh` sau P3 — đạt: headers API/frontend, cookie flags, validation/password policy, Origin/CSRF, session rotation/revocation on logout, lockout/error uniformity và thời gian tối thiểu cho email có/không tồn tại, GUID công khai, 1 MiB body cap, URL scheme, spoofed UserId, sở hữu chéo, phản ứng thu hồi session sau vi phạm lặp; dọn account test thành công, audit log được giữ lại.
- `./scripts/test-p2-smoke.sh` sau P4 — đạt toàn bộ luồng P2/P3 và xác nhận note/checklist round-trip, search sau giải mã, cột plaintext rỗng, nonce khác nhau giữa các trường, ciphertext cùng wrapped DEK có độ dài hợp lệ.
- `./scripts/test-p2-smoke.sh` sau P5 — tất cả kiểm tra P2–P4 đạt; xác minh Google OAuth entry point từ chối return URL ngoài và POST link yêu cầu CSRF. Do chưa cấu hình provider, test callback state được skip rõ ràng (callback chưa đăng ký trả 404).
- Với OAuth client ID/secret giả chỉ dùng local, xác minh authorization challenge có `state`, `nonce`, scope và PKCE `S256`; callback thiếu state được chuyển về frontend với lỗi chung. Không theo redirect tới Google và không thực hiện đăng nhập thật.
- Trước khi áp dụng P4, tạo tài khoản/ghi chú/checklist P3 có nội dung Unicode tiếng Việt/emoji; sau migration và khởi động API, đăng nhập/search/list trả lại chính xác dữ liệu cũ. Truy vấn DB xác nhận DEK/ciphertext tồn tại và nội dung plaintext cũ đã rỗng.
- `docker compose run --rm db-init` — migration `database/005_p5.sql` chạy lặp lại thành công; `notes_app` chỉ dùng các stored procedures được cấp quyền, không đọc trực tiếp bảng `external_logins`.
- Chưa chạy Google thật nên chưa xác nhận callback/token Google hoặc hoàn thành T18–T21 end-to-end.
- `docker compose up -d --build` — thành công; áp dụng `004_p4.sql`, API chạy migration dữ liệu và frontend/API hoạt động.
- `docker compose run --rm db-init` — các migration `001`–`004` chạy lặp lại thành công; kiểm tra quyền xác nhận `notes_app` không đọc trực tiếp bảng nhưng gọi được stored procedures. Restart API và `/api/health/ready` đều thành công.
- `dotnet format whitespace ... --include backend/SecureNotes.Api/Data/SqlNotesRepository.cs --no-restore` và `dotnet build backend/SecureNotes.Api/SecureNotes.Api.csproj --no-restore` — thành công, 0 lỗi.
- API và frontend trả security headers mong đợi; trình duyệt tải được trang đăng nhập với CSP đang bật.
- `docs/openapi.yaml` parse được thành YAML; `docker compose config --quiet`, `bash -n scripts/bootstrap-db.sh scripts/init-local-db.sh` và `git diff --check` hợp lệ.
- Release build API và production build frontend thành công; `./scripts/test-p2-smoke.sh`, hai smoke test P6, health/ready API và kiểm tra Swagger runtime đều đạt.
- `./scripts/test-p7-smoke.sh` đạt toàn bộ luồng P7, gồm kill switch tắt/mở; migration chạy lặp lại thành công, Swagger runtime có 28 path gồm đủ endpoint P7, health endpoint trả `ready`.
- `./scripts/test-p8-smoke.sh` — đạt: bật TOTP, recovery code dùng một lần, đăng nhập admin, làm mới role từ DB, khóa tài khoản, thu hồi phiên/share, audit không chứa nội dung note và quyền append-only của audit log.
- `docker compose run --rm db-init` — migration P8 chạy lặp lại thành công; `notes_app` bị từ chối đọc trực tiếp các bảng nhạy cảm và sửa audit log.
- Release build API và production build frontend thành công; frontend lint hoàn tất với 3 cảnh báo `react(set-state-in-effect)` trong `App.jsx`.
- `docs/openapi.yaml` parse thành YAML hợp lệ, có 40 path và 147 tham chiếu nội bộ hợp lệ; Compose config, shell syntax và `git diff --check` đều đạt.
- API/frontend chạy sau Docker build; readiness trả `ready`, Serilog tạo file trong volume `api-logs`.
- Build .NET còn cảnh báo dependency bắc cầu `System.Security.Cryptography.Pkcs` 9.0.18 có GHSA-846f-f764-76mx (High) và GHSA-2fmv-rv2c-59x7 (Moderate); chưa cập nhật dependency trong phạm vi P3.

## Giới hạn và việc còn lại

- Chưa thực hiện backup KEK ra khỏi môi trường local; operator cần lưu bí mật này trong secret manager/offline backup mã hóa trước production. Repository chưa có công cụ rewrap/rotation KEK tự động; không thay KEK nếu chưa có kế hoạch và công cụ đã kiểm chứng.
- HSTS chỉ được phát ở môi trường không phải Development; cấu hình Compose hiện tại vẫn dùng HTTP và không dành cho Internet. Trên production cần HTTPS và cấu hình `Security:AllowedOrigins` theo đúng domain frontend.
- Docker đang chạy `Development`; cookie Secure trong production được bật khi môi trường không phải Development. Không đưa cấu hình Compose hiện tại lên Internet.
- ASP.NET Data Protection key hiện ở filesystem container và không được lưu qua vòng đời container; cần thiết kế key ring bền vững/bảo vệ khóa trước staging/production.
- Chưa có Git remote; chưa xác minh gitleaks local.
- Build .NET còn advisory trên dependency bắc cầu `System.Security.Cryptography.Pkcs` 9.0.18: GHSA-846f-f764-76mx (High) và GHSA-2fmv-rv2c-59x7 (Moderate).
- Google Cloud project/test users/OAuth client chưa được tạo; chưa chạy login thật với Gmail và chưa xác nhận callback production/Google Console. P5 chỉ được xem là hoàn tất sau kiểm thử này.
- P8 smoke test chạy trong Compose; kiểm thử cấp admin trên SQL Server local ngoài Compose chưa chạy riêng.
