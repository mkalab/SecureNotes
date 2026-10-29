# SecureNotes

SecureNotes là website ghi chú với backend ASP.NET Core, React/Vite và SQL Server. Hợp đồng API hiện tại được ghi trong [docs/openapi.yaml](./docs/openapi.yaml) và chỉ mô tả các endpoint đã triển khai.

## Chạy trên Windows

**Cách khuyến nghị: Docker Desktop.** Cách này chạy DB, API và giao diện trong container, nên không cần cài .NET, Node.js hoặc SQL Server trên Windows.

### Cách 1: Chạy toàn bộ bằng Docker Desktop

**Chuẩn bị:** cài [Git for Windows](https://git-scm.com/downloads/win) và [Docker Desktop](https://www.docker.com/products/docker-desktop/). Mở Docker Desktop, chờ Docker báo đang chạy. Mở thư mục dự án trong File Explorer, nhấp phải và chọn **Open Git Bash here**.

**Lần đầu:** tạo file `.env` bằng Git Bash:

```bash
./scripts/init-dev-env.sh
```

File này chứa mật khẩu DB và khóa mã hóa riêng cho máy; script cũng bổ sung các biến mặc định có trong `.env.example`. Nếu `.env` đã tồn tại, script chỉ thêm biến còn thiếu, không ghi đè giá trị đã đặt. Giữ nguyên `.env` cho các lần chạy sau; không commit hoặc chia sẻ. Không xóa hay tạo lại file khi DB đã có dữ liệu mã hóa.

Mở PowerShell tại thư mục gốc dự án (trên File Explorer, gõ `powershell` vào thanh địa chỉ), rồi khởi động:

```powershell
docker compose up --build
```

Compose tự chạy SQL Server, khởi tạo schema, API và frontend. Khi các service đã khởi động, mở **http://localhost:5173** và đăng ký tài khoản. Lần sau chạy `docker compose up`; sau khi đổi code cần build lại, chạy `docker compose up --build`.

Dừng bằng `Ctrl+C` hoặc chạy `docker compose down` trong PowerShell khác tại thư mục dự án. Cả hai đều giữ dữ liệu DB. Chỉ chạy `docker compose down -v` nếu muốn xóa DB local. SQL Server Developer có thể cần ít nhất 2 GB RAM.

### Cách 2: Chạy API/frontend trên Windows, DB trong Docker

Dùng cách này khi cần debug code. Cài Docker Desktop, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) và [Node.js 22.12 trở lên](https://nodejs.org/).

**1. Tạo `.env` một lần** bằng Git Bash tại thư mục dự án:

```bash
./scripts/init-dev-env.sh
```

**2. Khởi động DB** từ PowerShell tại thư mục gốc:

```powershell
docker compose up -d db
docker compose run --rm db-init
```

**3. Chạy API** từ PowerShell tại thư mục gốc của dự án. Trước lần chạy đầu tiên, tạo và tin cậy chứng chỉ HTTPS phát triển (chỉ cần làm một lần):

```powershell
dotnet dev-certs https --trust
```

`.env` là file văn bản lưu các biến cấu hình. Khối lệnh sau đọc từng dòng `TEN_BIEN=gia_tri` trong file và đưa vào môi trường của **cửa sổ PowerShell hiện tại**; đây là thao tác “nạp `.env`”. Chạy khối này mỗi khi mở PowerShell mới để chạy API:

```powershell
Get-Content .env | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $name = $Matches[1]
        $value = $Matches[2]
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
}
$env:SecureNotes__KeyEncryptionKey = $env:SECURENOTES_KEK
$env:ConnectionStrings__SecureNotes = "Server=localhost,1433;Database=SecureNotes;User Id=notes_app;Password=$env:NOTES_APP_PASSWORD;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5"
dotnet run --project backend/SecureNotes.Api --launch-profile https
```

Lệnh `dotnet run` chạy API ở tiền cảnh, vì vậy hãy để cửa sổ đó mở trong lúc sử dụng ứng dụng; nhấn `Ctrl+C` để dừng. Các biến môi trường chỉ tồn tại trong cửa sổ PowerShell đã nạp `.env`. Nếu chạy API từ cửa sổ mới, chỉ cần chạy lại khối lệnh trên (khối lệnh sẽ tự đặt lại 2 biến API SecureNotes__KeyEncryptionKey và SECURENOTES_KEK). Giữ nguyên file `.env` và giá trị `SECURENOTES_KEK` khi DB đã có dữ liệu mã hóa.

**4. Chạy frontend** trong cửa sổ PowerShell khác, tại thư mục gốc:
nạp `.env` và bao gồm nạp `.env` đặt hai biến API ở trên.
```powershell
Set-Location frontend
npm ci
npm run dev
```

`npm ci` chỉ cần sau khi clone hoặc khi dependencies thay đổi; lần sau chỉ cần `npm run dev`. Mở **http://localhost:5173**. Không chạy API/frontend bằng Compose đồng thời với API/frontend local vì sẽ xung đột cổng.

### Cách 3: Dùng SQL Server cài trên Windows

Cài SQL Server Developer/Express và `sqlcmd`. Bật SQL Server Authentication (mixed mode), TCP/IP và cổng TCP `1433`, rồi khởi động dịch vụ SQL Server. Tạo `.env` bằng Git Bash như Cách 2. Mặc định script dùng login `sa` với mật khẩu `MSSQL_SA_PASSWORD`; bạn có thể dùng login quản trị khác nếu login đó thuộc server role `sysadmin`. Để dùng tài khoản khác, thêm vào `.env`:

```dotenv
SECURENOTES_SQL_ADMIN_USER=ten_login_quan_tri
SECURENOTES_SQL_ADMIN_PASSWORD=mat_khau_login_quan_tri
```

Không để trống hai giá trị này sau dấu `=`. Login quản trị chỉ dùng để tạo database và các login ứng dụng; migration sau đó chạy bằng `notes_migrator`. Nếu SQL Server dùng host/cổng khác, sửa `SECURENOTES_SQL_SERVER` trong `.env`.

Script `scripts/init-local-db.sh` cần Bash. Chạy trong Git Bash tại thư mục dự án, bảo đảm `sqlcmd` và `cygpath` có trong `PATH` của Git Bash, đồng thời SQL Server cho phép kết nối TCP:

```bash
./scripts/init-local-db.sh
```

`cygpath` thường được cài cùng Git for Windows; script dùng nó để đổi đường dẫn file SQL tạm sang định dạng Windows trước khi gọi `sqlcmd.exe`. Nếu gặp lỗi mở file `C:` với thông báo `Access is denied`, hãy cập nhật script và kiểm tra `cygpath` bằng lệnh `cygpath -w /tmp`.

Nếu API báo `Could not find stored procedure 'app.DeleteExpiredNoteAttachmentReservations'`, DB chưa có migration P6. Mở Git Bash tại thư mục gốc của repo mới nhất và chạy lại `./scripts/init-local-db.sh`; đợi thông báo `Local SQL Server database is ready`, sau đó chạy API lại từ PowerShell. Không cần xóa database hoặc tạo lại `.env`; script giữ dữ liệu hiện có và áp dụng các migrations còn thiếu.

**Chạy API:** mở PowerShell tại thư mục gốc dự án (thư mục chứa `README.md` và `docker-compose.yml`). Trước lần chạy đầu tiên, tạo chứng chỉ HTTPS phát triển một lần:

```powershell
dotnet dev-certs https --trust
```

`.env` lưu cấu hình trên đĩa; khối lệnh dưới đây nạp các biến vào cửa sổ PowerShell hiện tại. Chạy lại trong mỗi PowerShell mới, rồi đặt khóa mã hóa và connection string trỏ đến SQL Server local:

```powershell
Get-Content .env | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $name = $Matches[1]
        $value = $Matches[2]
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
}
$env:SecureNotes__KeyEncryptionKey = $env:SECURENOTES_KEK
$env:ConnectionStrings__SecureNotes = "Server=$env:SECURENOTES_SQL_SERVER;Database=SecureNotes;User Id=notes_app;Password=$env:NOTES_APP_PASSWORD;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5"
dotnet run --project backend/SecureNotes.Api --launch-profile https
```

`dotnet run` chạy API ở tiền cảnh; để cửa sổ mở và nhấn `Ctrl+C` để dừng. **Chạy frontend** như Bước 4 ở Cách 2: mở PowerShell khác tại thư mục gốc, chạy `Set-Location frontend`, rồi `npm run dev` (chạy `npm ci` sau clone hoặc khi dependencies đổi). Mở **http://localhost:5173**. Không commit hoặc chia sẻ `.env`.

## Chạy trên Linux

Chọn một trong ba cách sau. **Nếu chỉ muốn chạy thử, dùng Docker Compose (Cách 1).**

| Cách chạy | Cần cài | Phù hợp khi |
|---|---|---|
| Docker Compose | Docker Engine, Compose v2 | Chạy toàn bộ ứng dụng nhanh |
| API/frontend local, DB trong Docker | Docker, .NET 10 SDK, Node.js 22.12+ | Phát triển hoặc debug code |
| Tất cả chạy local | .NET 10 SDK, Node.js 22.12+, SQL Server, `sqlcmd` | Không muốn dùng Docker |

Các lệnh chạy từ thư mục gốc dự án, trừ khi có ghi chú khác.

### Cách 1: Chạy toàn bộ bằng Docker Compose

Compose tự khởi động SQL Server, tạo/cập nhật schema, API và giao diện web. Cần Docker Engine và Docker Compose v2.

**Lần đầu trên máy:**

```bash
./scripts/init-dev-env.sh
docker compose up --build
```

Script tạo `.env` chứa mật khẩu DB và khóa mã hóa riêng cho máy, đồng thời bổ sung các biến mặc định còn thiếu từ `.env.example` mà không ghi đè giá trị hiện có. Giữ nguyên file này cho các lần chạy sau; không commit hoặc chia sẻ. `.env.example` là mẫu cấu hình, không chứa bí mật. SQL Server Developer có thể cần ít nhất 2 GB RAM.

Khi các service đã khởi động, mở **http://localhost:5173** và đăng ký tài khoản.

**Các lần sau:**

```bash
docker compose up
```

Sau khi thay đổi code, chạy `docker compose up --build` để build lại. Dừng bằng `Ctrl+C` hoặc `docker compose down`; cả hai đều giữ dữ liệu DB. Chỉ chạy `docker compose down -v` nếu muốn xóa DB local.

### Cách 2: API/frontend local, SQL Server bằng Docker

API và frontend chạy trên máy; chỉ SQL Server chạy trong Docker. Cần Docker Engine/Compose v2, .NET 10 SDK và Node.js 22.12 trở lên.

**1. Tạo cấu hình (chỉ làm lần đầu):**

```bash
./scripts/init-dev-env.sh
```

**2. Khởi động DB và chạy migrations** (mỗi lần cần dùng DB):

```bash
docker compose up -d db
docker compose run --rm db-init
```

**3. Chạy API** trong terminal tại thư mục gốc. Trước lần chạy API đầu tiên, tạo và tin cậy chứng chỉ HTTPS phát triển (chỉ cần làm một lần):

```bash
dotnet dev-certs https --trust
```

Sau đó nạp `.env` mỗi lần mở terminal mới và chạy API:

```bash
set -a
. ./.env
set +a
export SecureNotes__KeyEncryptionKey="$SECURENOTES_KEK"
export ConnectionStrings__SecureNotes="Server=localhost,1433;Database=SecureNotes;User Id=notes_app;Password=${NOTES_APP_PASSWORD};Encrypt=True;TrustServerCertificate=True;Connect Timeout=5"
dotnet run --project backend/SecureNotes.Api --launch-profile https
```

`dotnet run` không tự đọc `.env`; cần nạp lại cấu hình mỗi khi mở terminal mới. Connection string kết nối tới SQL Server ở `localhost,1433`. Giữ nguyên `.env` và `SECURENOTES_KEK` khi DB đã có dữ liệu mã hóa.

**4. Chạy frontend** trong terminal khác:

```bash
cd frontend
npm ci
npm run dev
```

`npm ci` chỉ cần sau khi clone hoặc khi dependencies thay đổi; các lần sau chỉ cần `npm run dev`.

Nếu đang dùng Docker Compose chạy API/frontend, dừng chúng trước khi chạy API/frontend trực tiếp để tránh xung đột cổng. Mở **http://localhost:5173**.

### Cách 3: Chạy toàn bộ local, không Docker

Ở cách này, API, frontend và SQL Server đều chạy trên máy. Cần .NET 10 SDK, Node.js 22.12 trở lên, npm, SQL Server Developer/Express và `sqlcmd`. Bật SQL Server Authentication (mixed mode), TCP/IP và cổng TCP `1433`, rồi khởi động dịch vụ SQL Server.

**1. Tạo cấu hình và bí mật local:**

```bash
./scripts/init-dev-env.sh
```

Script tạo `.env` với mật khẩu ngẫu nhiên cho SQL logins và khóa mã hóa. Đặt mật khẩu login `sa` trong SQL Server bằng giá trị `MSSQL_SA_PASSWORD` trong file này để script khởi tạo DB kết nối được. Nếu SQL Server dùng host/cổng khác, sửa `SECURENOTES_SQL_SERVER` trong `.env` (mặc định `localhost,1433`). Không chia sẻ hoặc commit `.env`.

**2. Tạo DB và chạy migrations:**

```bash
./scripts/init-local-db.sh
```

Script tạo database nếu chưa có, tạo các login cần thiết, chạy/cập nhật migrations `001`–`007` và kiểm tra quyền truy cập. Chạy lại sau khi cập nhật mã nguồn để áp dụng migrations mới; script không xóa dữ liệu hiện có. Đảm bảo SQL Server cho phép kết nối TCP từ máy đang chạy script.

**3. Chạy API** trong terminal tại thư mục gốc. Nạp `.env` mỗi khi mở terminal mới:

```bash
set -a
. ./.env
set +a
export SecureNotes__KeyEncryptionKey="$SECURENOTES_KEK"
export ConnectionStrings__SecureNotes="Server=${SECURENOTES_SQL_SERVER};Database=SecureNotes;User Id=notes_app;Password=${NOTES_APP_PASSWORD};Encrypt=True;TrustServerCertificate=True;Connect Timeout=5"
```

`SECURENOTES_SQL_SERVER` mặc định là `localhost,1433`. Nếu SQL Server dùng cổng khác, cập nhật biến đó trong `.env`. Không ghi connection string chứa mật khẩu vào `appsettings.json` hoặc Git.

```bash
dotnet dev-certs https --trust
dotnet run --project backend/SecureNotes.Api --launch-profile https
```

`dotnet dev-certs https --trust` chỉ cần chạy một lần trên máy. `dotnet run` không tự đọc `.env`, nên phải nạp lại cấu hình trong terminal mới. Nếu SQL Server dùng chứng chỉ TLS hợp lệ, có thể bỏ `TrustServerCertificate=True`; tùy chọn này chỉ dành cho môi trường phát triển.

**4. Chạy frontend** trong terminal khác:

```bash
cd frontend
npm ci
npm run dev
```

`npm ci` chỉ cần sau khi clone hoặc khi dependencies thay đổi; các lần sau chạy `npm run dev`. Mở **http://localhost:5173**. Giữ nguyên `.env` và `SECURENOTES_KEK`; thay hoặc mất khóa này có thể làm dữ liệu đã mã hóa không thể giải mã.

Sau khi ứng dụng chạy, mở giao diện và đăng ký bằng email cùng mật khẩu (tối thiểu 12 ký tự, gồm chữ hoa, chữ thường, số và ký tự đặc biệt), rồi tạo ghi chú và danh mục. Mật khẩu được băm PBKDF2-HMAC-SHA256. Phiên đăng nhập dùng cookie HttpOnly, SameSite=Strict, session ID ngẫu nhiên 256 bit lưu phía server dưới dạng hash; mỗi tài khoản có tối đa một phiên với timeout idle 20 phút và tuyệt đối 8 giờ. Các thao tác thay đổi dữ liệu cần `X-CSRF-TOKEN` và Origin hợp lệ. Cấu hình Compose chạy ở `Development`, không dùng để triển khai production.

API giới hạn body thông thường ở 1 MiB; upload tệp được giới hạn riêng theo cấu hình. Rate limit đăng nhập/đăng ký và upload; lỗi đăng nhập dùng thông báo chung. Security headers được thêm cho API và frontend dev/preview. Riêng Vite dev server cho phép inline script để React Fast Refresh hoạt động; frontend preview giữ CSP chặt hơn (`script-src 'self'`). Mọi API response không được cache. Cấu hình production phải đặt `Security:AllowedOrigins` chính xác cho các origin frontend công khai, dùng HTTPS và không cho phép origin wildcard.

### Tệp đính kèm ghi chú (P6)

Mỗi ghi chú hỗ trợ tối đa 20 tệp; mỗi tệp tối đa 10 MiB và mỗi tài khoản tối đa 100 MiB. Định dạng được chấp nhận: PDF, TXT, MD, PNG, JPG/JPEG. Server kiểm tra phần mở rộng, MIME và nội dung; ảnh được chuẩn hóa trước khi lưu. Tệp được mã hóa AES-GCM bằng khóa dữ liệu của tài khoản và lưu dưới dạng ciphertext ngoài web root. Khi tải xuống, tệp luôn được trả về dưới dạng download, không hiển thị PDF trực tiếp trên trang.

Compose bật tính năng theo mặc định và tự chạy migration P6 khi khởi tạo DB. Dữ liệu tệp nằm trong volume `attachments-data`, tách biệt với volume SQL Server. `docker compose down` giữ cả hai; `docker compose down -v` xóa cả database lẫn tệp đính kèm. Không xóa volume này nếu cần giữ tệp.

Để tắt tính năng trên máy local, đặt `SECURENOTES_ATTACHMENTS_ENABLED=false` trong `.env` rồi khởi động lại API. Các biến `SECURENOTES_ATTACHMENTS_MAX_FILE_BYTES`, `SECURENOTES_ATTACHMENTS_MAX_FILES_PER_NOTE` và `SECURENOTES_ATTACHMENTS_MAX_TOTAL_BYTES_PER_USER` đặt giới hạn Compose; giá trị mặc định lần lượt là 10 MiB, 20 tệp và 100 MiB. Khi chạy API trực tiếp, dùng cấu hình `Attachments:MaxFileBytes`, `Attachments:MaxFilesPerNote` và `Attachments:MaxTotalBytesPerUser`. `Attachments:StoragePath` phải là đường dẫn tuyệt đối ngoài `wwwroot`; Compose local luôn dùng volume riêng `attachments-data`.

Khi stack đang chạy, từ Git Bash trên Windows hoặc terminal Linux tại thư mục gốc dự án, có thể kiểm tra các luồng TXT/MD/PDF/PNG/JPEG, tải xuống, phân quyền, MIME/phần mở rộng, lưu ciphertext và giới hạn 10 MiB bằng `./scripts/test-p6-smoke.sh`. Script tạo và tự dọn các tài khoản/dữ liệu kiểm thử. Để kiểm tra quota khi hai upload chạy đồng thời, chạy `./scripts/test-p6-quota-smoke.sh`; script tạm khởi động lại API với giới hạn một tệp/ghi chú, sau đó khôi phục cấu hình từ `.env`.

### Chia sẻ ghi chú qua link (P7)

Mở ghi chú rồi tạo link với quyền **Chỉ xem** hoặc **Được sửa nội dung**; link hết hạn mặc định sau 24 giờ, có thể chọn 7 hoặc 30 ngày. Token ngẫu nhiên 32 byte chỉ hiển thị một lần lúc tạo; DB chỉ lưu SHA-256 hash. Giữ link ở nơi an toàn và thu hồi tại phần chia sẻ của ghi chú bất cứ lúc nào. Link hết hạn, sai hoặc bị thu hồi đều trả cùng lỗi 404.

Link chỉ xem cho phép đọc nội dung dạng text và tải tệp đính kèm của đúng ghi chú. Link sửa yêu cầu người nhận đăng nhập và chỉ sửa được tiêu đề/nội dung; không cấp quyền tải lên/xóa tệp hay thay đổi thuộc tính ghi chú. Các trang link không render HTML/Markdown, phản hồi API không được cache và truy vấn link bị giới hạn theo IP. Có thể tắt nhanh bằng `SECURENOTES_SHARING_ENABLED=false` trong `.env`, sau đó khởi động lại API.

Migration `database/007_p7.sql` được tự động áp dụng khi Compose khởi tạo DB. Để kiểm tra token hash, quyền view/edit, đăng nhập, thu hồi/hết hạn, phạm vi file, audit và rate limit, chạy `./scripts/test-p7-smoke.sh` từ Git Bash trên Windows hoặc terminal Linux khi stack đang hoạt động. Script tự dọn dữ liệu test.

### Quản trị và nhật ký (P8)

Mọi tài khoản mới có vai trò `user`; trường `role` gửi thêm khi đăng ký bị từ chối. Để tạo admin: đăng nhập tài khoản cần cấp quyền, mở **Xác minh hai bước** ở thanh bên và bật TOTP; sau đó tại Git Bash/terminal Linux ở thư mục gốc chạy `./scripts/grant-admin.sh email@example.com`. Script chỉ nhận email tường minh, chỉ cấp admin cho tài khoản đã bật 2FA, thu hồi các phiên hiện tại và ghi audit. Đăng nhập lại bằng mật khẩu hoặc Google rồi xác minh mã TOTP; mã dự phòng dùng một lần có thể dùng khi mất ứng dụng xác thực. Admin không thể tắt 2FA trong khi còn vai trò admin.

Nút **Quản trị** chỉ xuất hiện với admin đã xác minh 2FA trong phiên hiện tại. Bảng này cho phép xem danh sách tài khoản, khóa/mở khóa, buộc đăng xuất, xem nhật ký và thu hồi link chia sẻ; không có API đọc nội dung ghi chú hay tệp. Vai trò được đọc lại từ DB ở mỗi request đã xác thực nên thay đổi role/khóa tài khoản có hiệu lực ngay.

Audit log ghi các sự kiện đăng nhập, khóa tài khoản, xác minh hai bước, liên kết Google, tải tệp, chia sẻ và thao tác admin. `notes_app` bị từ chối UPDATE/DELETE trên `dbo.audit_logs`. Log ứng dụng Serilog được xoay theo ngày, giới hạn 10 MiB mỗi file và giữ tối đa 14 file; Compose lưu trong volume `api-logs` (độc lập với DB/file đính kèm). Không ghi request body, mật khẩu, mã TOTP/recovery, token chia sẻ hay nội dung ghi chú. Chuỗi băm audit là mục COULD của roadmap, chưa triển khai.

Migration `database/008_p8.sql` được áp dụng tự động bởi Compose; khi dùng SQL Server local chạy `./scripts/init-local-db.sh` từ Git Bash để cập nhật. Kiểm tra RBAC, MFA, audit append-only và hành động admin bằng `./scripts/test-p8-smoke.sh` khi stack Compose đang chạy. Script tạo và tự dọn tài khoản/dữ liệu thử nghiệm.

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

Các biến OAuth đã có sẵn trong `.env` sau khi chạy `init-dev-env.sh`; điền giá trị thật nếu muốn bật đăng nhập Google. Không đưa Client Secret vào file example hoặc Git.

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
