# SecureNotes

SecureNotes là website ghi chú với backend ASP.NET Core, React/Vite và SQL Server. Hợp đồng API hiện tại được ghi trong [docs/openapi.yaml](./docs/openapi.yaml) và chỉ mô tả các endpoint đã triển khai; endpoint roadmap cho giai đoạn sau sẽ được thêm khi thực hiện.

## Chạy bằng Docker Compose

Yêu cầu Docker Engine và Docker Compose v2. SQL Server chỉ nằm trong mạng nội bộ của Compose; chỉ giao diện web được bind vào `127.0.0.1`.

```bash
./scripts/init-dev-env.sh
docker compose up --build
```

Mở `http://localhost:5173`. Script tạo mật khẩu SQL ngẫu nhiên trong `.env` với quyền file chỉ chủ sở hữu đọc/ghi. Không commit file này. SQL Server Developer có thể cần ít nhất 2 GB RAM.

Đăng ký bằng email và mật khẩu (tối thiểu 12 ký tự, gồm chữ hoa, chữ thường, số và ký tự đặc biệt), sau đó tạo ghi chú và danh mục trong giao diện. Mật khẩu được băm PBKDF2-HMAC-SHA256. Phiên đăng nhập dùng cookie HttpOnly, SameSite=Strict, session ID ngẫu nhiên 256 bit lưu phía server dưới dạng hash; mỗi tài khoản có tối đa một phiên với timeout không hoạt động 20 phút và tuyệt đối 8 giờ. Các thao tác thay đổi dữ liệu cần `X-CSRF-TOKEN` và Origin hợp lệ. Môi trường Compose chạy `Development` và chỉ bind frontend vào loopback; cookie Secure được bật ở production qua HTTPS, không dùng cấu hình Compose như môi trường production.

API giới hạn body ở 1 MiB, rate limit đăng nhập/đăng ký và trả lỗi chung khi đăng nhập thất bại. Security headers được thêm cho API và frontend dev/preview; mọi API response không được cache. Cấu hình production phải đặt `Security:AllowedOrigins` chính xác cho các origin frontend công khai, dùng HTTPS và không cho phép origin wildcard.

### Bật đăng nhập Google (P5)

Đăng nhập Google bị tắt nếu thiếu cấu hình OAuth hợp lệ; các luồng email/mật khẩu vẫn hoạt động bình thường. Để bật:

1. Tạo Google Cloud project, cấu hình OAuth consent screen kiểu External ở chế độ Testing, chỉ yêu cầu `openid`, `email`, `profile`, rồi thêm các Gmail được phép ở Audience → Test users.
2. Tạo OAuth Client ID kiểu Web application. Đăng ký URI callback khớp tuyệt đối với môi trường:
   - Docker Compose local: `http://localhost:5173/signin-oidc`
   - API chạy theo profile HTTPS local: `https://localhost:7139/signin-oidc`
   - Production: `https://<frontend-domain>/signin-oidc`
3. Đưa Client ID, Client Secret và callback URI vào secret manager hoặc `.env` local (không commit):

```dotenv
GOOGLE_OIDC_CLIENT_ID=...
GOOGLE_OIDC_CLIENT_SECRET=...
GOOGLE_OIDC_REDIRECT_URI=http://localhost:5173/signin-oidc
GOOGLE_OIDC_FRONTEND_ORIGIN=http://localhost:5173
```

`GOOGLE_OIDC_FRONTEND_ORIGIN` phải khớp chính xác một origin trong `Security:AllowedOrigins`. Với chạy local không Docker, đặt secrets bằng:

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "..." --project backend/SecureNotes.Api
dotnet user-secrets set "Authentication:Google:ClientSecret" "..." --project backend/SecureNotes.Api
dotnet user-secrets set "Authentication:Google:RedirectUri" "https://localhost:7139/signin-oidc" --project backend/SecureNotes.Api
dotnet user-secrets set "Authentication:Google:FrontendOrigin" "http://localhost:5173" --project backend/SecureNotes.Api
```

Không ghi Client Secret vào `appsettings*.json`. API dùng Authorization Code + PKCE, xác minh `state`, `nonce`, chữ ký/issuer/audience/thời hạn của ID token, chỉ nhận `email_verified=true`, định danh bằng `sub` và không lưu token Google. Người dùng mới có tài khoản không mật khẩu; email trùng tài khoản hiện hữu không được tự liên kết. Chủ tài khoản mật khẩu có thể xác thực lại ở Cài đặt liên kết; yêu cầu liên kết được bảo vệ bởi session hiện hành, Origin và CSRF.

Khi chạy sau reverse proxy, đặt `ForwardedHeaders:KnownProxies` thành địa chỉ IP chính xác của proxy tin cậy (Compose: `SECURENOTES_TRUSTED_PROXY_IPS`). API chỉ đọc tối đa một bộ `X-Forwarded-For/Proto/Host` từ proxy đã khai báo; không tin forwarded headers từ client tùy ý. Ứng dụng Google ở Testing chỉ cho phép Gmail trong Test users. Việc tạo Google Cloud project/client và thêm test users cần operator thực hiện; code không có nhánh giả lập đăng nhập.

Ghi chú, danh mục và mục con dùng SQL Server qua stored procedures, với lọc chủ sở hữu trong từng thao tác. Tiêu đề/nội dung ghi chú và tiêu đề checklist được mã hóa AES-256-GCM bằng DEK riêng mỗi tài khoản; DEK được bọc bằng KEK 32 byte. API giải mã sau khi xác thực chủ sở hữu, và tìm kiếm diễn ra trong ứng dụng sau giải mã. Migration P4 tự mã hóa dữ liệu P2/P3 cũ khi API khởi động, rồi xóa nội dung plaintext khỏi các cột cũ. Không có TOTP secret trong bản hiện tại vì luồng 2FA chưa được triển khai.

`./scripts/init-dev-env.sh` tạo `SECURENOTES_KEK` base64 32 byte trong `.env` với quyền file `600`. Giữ nguyên giá trị này qua các lần khởi động; mất hoặc thay KEK mà chưa bọc lại DEK sẽ khiến nội dung không thể giải mã. Trước khi triển khai thật, lưu bản sao KEK trong secret manager/offline backup được mã hóa và tách khỏi máy chủ/backup cơ sở dữ liệu. Quy trình xoay khóa: dừng API trong maintenance, sao lưu DB và KEK cũ, dùng công cụ vận hành offline đã được kiểm chứng để mở từng DEK bằng KEK cũ rồi bọc bằng KEK mới, xác minh mọi DEK trước khi cập nhật secret/restart API, và giữ bản cũ cho đến khi hết thời hạn khôi phục backup. Repository hiện chưa có công cụ xoay KEK tự động; không thay KEK trước khi chuẩn bị công cụ và kế hoạch khôi phục.

Khi nâng DB P2/P3 hiện có lên P4, backup DB trước và dừng API để tránh request đồng thời với migration plaintext:

```bash
docker compose stop api
docker compose run --rm db-init
docker compose up -d --build api frontend
```

API mã hóa dữ liệu legacy trước khi bắt đầu phục vụ request; nếu migration không hoàn tất, API không khởi động.

Dự án không cung cấp baseline cố ý dễ tổn thương để triển khai hoặc thử trên mạng.

Dừng bằng `Ctrl+C`; chạy `docker compose down` để dọn container và giữ dữ liệu DB. Chỉ dùng `docker compose down -v` khi chủ động muốn xóa cả dữ liệu DB local.

## Chạy local không dùng Docker

Yêu cầu .NET 10 SDK, Node.js 22 (tối thiểu 22.12.0) và npm. Phiên bản Node được khai báo trong `.nvmrc`; nếu dùng nvm, chạy `nvm install` và `nvm use` ở thư mục gốc. Tin cậy chứng chỉ HTTPS phát triển một lần:

```bash
dotnet dev-certs https --trust
```

Chạy API và frontend trong hai terminal riêng:

```bash
dotnet run --project backend/SecureNotes.Api --launch-profile https
```

```bash
cd frontend && npm ci && npm run dev
```

Mở `http://localhost:5173`. Frontend gọi `/api/health` qua Vite proxy tới API HTTPS ở `https://localhost:7139`.
API local cũng cần `SecureNotes__KeyEncryptionKey` là base64 của đúng 32 byte và cơ sở dữ liệu đã chạy các migration `database/001_schema.sql` đến `database/005_p5.sql`. Dùng cùng KEK cho mọi lần chạy và sao lưu giá trị đó riêng khỏi DB.

Kiểm tra trực tiếp API:

```bash
curl --insecure https://localhost:7139/api/health
```

`--insecure` chỉ dùng cho chứng chỉ phát triển local. Không dùng tùy chọn này trong production.

## Kiểm tra build

```bash
dotnet build backend/SecureNotes.Api/SecureNotes.Api.csproj
cd frontend && npm run build
```

Sau khi stack Docker Compose đã chạy, có thể kiểm tra luồng P2–P5 (CRUD/search, ciphertext lưu trong DB, wrapped DEK, OAuth challenge/return URL an toàn) và các biện pháp P3 bằng:

```bash
./scripts/test-p2-smoke.sh
```

Script dùng các tài khoản tạm thời và xóa chúng cùng dữ liệu thử nghiệm khi kết thúc. Nó yêu cầu `.env`, Docker Compose, `curl`, `node` và `openssl`.

## Quy ước Git

- `main` là nhánh ổn định; làm việc trên nhánh `feature/...` và review trước khi merge.
- Không commit secrets hoặc file `.env`; chỉ chia sẻ cấu hình mẫu không chứa giá trị bí mật.
- Không commit bản baseline dễ tổn thương lên môi trường triển khai.
- Cài [gitleaks](https://github.com/gitleaks/gitleaks#installing), rồi chạy `./scripts/install-git-hooks.sh` để bật quét secret trước mỗi commit. CI cũng quét repository và build/lint cả backend lẫn frontend.

## Tài liệu

- [Roadmap](./roadmap_attw.md)
- [OpenAPI draft](./docs/openapi.yaml)
- [Current state](./current_state.md)
