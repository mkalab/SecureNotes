import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import './App.css'

const colors = ['#fff8c5', '#fde2e2', '#dbeafe', '#dcfce7', '#f3e8ff', '#ffffff']
const googleAuthErrors = {
  google_unavailable: 'Đăng nhập Google chưa được cấu hình trên máy chủ này.',
  google_failed: 'Không thể hoàn tất đăng nhập Google. Hãy thử lại hoặc đăng nhập bằng email và mật khẩu.',
  google_link_failed: 'Không thể liên kết tài khoản Google. Hãy kiểm tra mật khẩu và thử lại.',
  admin_two_factor_required: 'Tài khoản quản trị phải bật xác minh hai bước trước khi đăng nhập.',
}
const googleAuthStatuses = {
  google_linked: 'Đã liên kết tài khoản Google. Lần sau bạn có thể đăng nhập bằng Google.',
  two_factor_required: 'Nhập mã từ ứng dụng xác thực hoặc một mã dự phòng.',
}

async function request(path, options = {}) {
  const headers = new Headers(options.headers)
  if (options.body && !(options.body instanceof FormData)) headers.set('Content-Type', 'application/json')

  if (options.method === 'POST') {
    const csrf = await fetch('/api/csrf', { credentials: 'same-origin' })
    if (!csrf.ok) throw new Error('Không lấy được mã bảo vệ CSRF.')
    const { requestToken } = await csrf.json()
    headers.set('X-CSRF-TOKEN', requestToken)
  }

  const response = await fetch(path, {
    ...options,
    headers,
    credentials: 'same-origin',
  })

  if (response.status === 204) return null
  const data = await response.json().catch(() => null)
  if (!response.ok) {
    throw new Error(data?.error ?? data?.title ?? `Yêu cầu thất bại (${response.status}).`)
  }
  return data
}

function AttachmentPanel({ noteId, onError }) {
  const [attachments, setAttachments] = useState([])
  const [busy, setBusy] = useState(false)
  const [panelError, setPanelError] = useState('')
  const fileInput = useRef(null)

  useEffect(() => {
    let active = true
    request(`/api/notes/${noteId}/attachments`)
      .then((result) => {
        if (active) setAttachments(result)
      })
      .catch((loadError) => {
        if (active) setPanelError(loadError.message)
      })
    return () => {
      active = false
    }
  }, [noteId])

  const loadAttachments = useCallback(async () => {
    setAttachments(await request(`/api/notes/${noteId}/attachments`))
  }, [noteId])

  async function uploadFile(event) {
    const [file] = event.target.files ?? []
    event.target.value = ''
    if (!file) return

    setBusy(true)
    setPanelError('')
    try {
      if (file.size > 10 * 1024 * 1024) {
        throw new Error('Mỗi tệp được giới hạn tối đa 10 MiB.')
      }

      const body = new FormData()
      body.append('file', file)
      await request(`/api/notes/${noteId}/attachments`, { method: 'POST', body })
      await loadAttachments()
    } catch (uploadError) {
      setPanelError(uploadError.message)
      onError(uploadError.message)
    } finally {
      setBusy(false)
    }
  }

  async function deleteAttachment(attachment) {
    if (!window.confirm(`Xóa tệp "${attachment.fileName}"?`)) return
    setPanelError('')
    try {
      await request(`/api/notes/${noteId}/attachments/${attachment.attachmentId}/delete`, { method: 'POST' })
      await loadAttachments()
    } catch (deleteError) {
      setPanelError(deleteError.message)
      onError(deleteError.message)
    }
  }

  return (
    <section className="attachments-panel" aria-label="Tệp đính kèm">
      <div className="attachments-heading">
        <strong>Tệp đính kèm</strong>
        <button className="attachment-add" type="button" disabled={busy}
          onClick={() => fileInput.current?.click()}>
          {busy ? 'Đang tải…' : '+ Thêm tệp'}
        </button>
        {account.role === 'admin' && (
          <button className={`nav-item ${adminMode ? 'active' : ''}`} onClick={() => setAdminMode((current) => !current)}>
            <span aria-hidden="true">⚙</span> Quản trị
          </button>
        )}
        <input ref={fileInput} className="attachment-input" type="file"
          accept=".pdf,.txt,.md,.png,.jpg,.jpeg,application/pdf,text/plain,text/markdown,image/png,image/jpeg"
          onChange={uploadFile} disabled={busy} />
      </div>
      {panelError && <p className="attachment-error" role="alert">{panelError}</p>}
      {attachments.length === 0 ? (
        <p className="attachment-empty">Chưa có tệp. Hỗ trợ PDF, TXT, MD, PNG và JPEG; tối đa 10 MiB/tệp.</p>
      ) : (
        <ul className="attachment-list">
          {attachments.map((attachment) => (
            <li key={attachment.attachmentId}>
              <a href={`/api/notes/${noteId}/attachments/${attachment.attachmentId}/download`}>
                {attachment.fileName}
              </a>
              <span>{formatBytes(attachment.sizeBytes)}</span>
              <button type="button" className="icon-button" onClick={() => deleteAttachment(attachment)}
                aria-label={`Xóa tệp ${attachment.fileName}`}>×</button>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function SharePanel({ noteId, onError }) {
  const [shares, setShares] = useState([])
  const [permission, setPermission] = useState('view')
  const [expiresInHours, setExpiresInHours] = useState(24)
  const [createdUrl, setCreatedUrl] = useState('')
  const [status, setStatus] = useState('')
  const [busy, setBusy] = useState(false)

  const loadShares = useCallback(async () => {
    setShares(await request(`/api/notes/${noteId}/shares`))
  }, [noteId])

  useEffect(() => {
    let active = true
    request(`/api/notes/${noteId}/shares`)
      .then((result) => {
        if (active) setShares(result)
      })
      .catch((loadError) => {
        if (active) onError(loadError.message)
      })
    return () => {
      active = false
    }
  }, [noteId, onError])

  async function createShare(event) {
    event.preventDefault()
    setBusy(true)
    setCreatedUrl('')
    setStatus('')
    try {
      const created = await request(`/api/notes/${noteId}/shares`, {
        method: 'POST',
        body: JSON.stringify({ permission, expiresInHours }),
      })
      setCreatedUrl(`${window.location.origin}/share/${created.token}`)
      setStatus(`Link ${created.permission} có hiệu lực đến ${new Date(created.expiresAt).toLocaleString('vi-VN')}.`)
      await loadShares()
    } catch (createError) {
      onError(createError.message)
    } finally {
      setBusy(false)
    }
  }

  async function revokeShare(share) {
    setBusy(true)
    try {
      await request(`/api/notes/${noteId}/shares/${share.shareId}/revoke`, { method: 'POST' })
      setStatus('Đã thu hồi link chia sẻ.')
      await loadShares()
    } catch (revokeError) {
      onError(revokeError.message)
    } finally {
      setBusy(false)
    }
  }

  async function copyCreatedUrl() {
    try {
      await navigator.clipboard.writeText(createdUrl)
      setStatus('Đã sao chép link. Hãy lưu lại; link chỉ được hiển thị khi tạo.')
    } catch {
      onError('Không thể sao chép link trên trình duyệt này. Hãy chọn và sao chép link thủ công.')
    }
  }

  return (
    <section className="share-panel" aria-label="Chia sẻ ghi chú">
      <h3>Chia sẻ qua link</h3>
      <form className="share-create-form" onSubmit={createShare}>
        <label>
          Quyền
          <select value={permission} onChange={(event) => setPermission(event.target.value)} disabled={busy}>
            <option value="view">Chỉ xem</option>
            <option value="edit">Được sửa nội dung</option>
          </select>
        </label>
        <label>
          Hết hạn sau
          <select value={expiresInHours} onChange={(event) => setExpiresInHours(Number(event.target.value))} disabled={busy}>
            <option value={24}>24 giờ</option>
            <option value={168}>7 ngày</option>
            <option value={720}>30 ngày</option>
          </select>
        </label>
        <button className="secondary-button" disabled={busy}>
          {busy ? 'Đang xử lý…' : 'Tạo link'}
        </button>
      </form>
      {createdUrl && (
        <div className="share-created" role="status">
          <p>{status}</p>
          <input aria-label="Link chia sẻ mới" readOnly value={createdUrl} onFocus={(event) => event.target.select()} />
          <button type="button" className="secondary-button" onClick={copyCreatedUrl}>Sao chép</button>
        </div>
      )}
      {!createdUrl && status && <p className="share-status" role="status">{status}</p>}
      {shares.length > 0 && (
        <ul className="share-list">
          {shares.map((share) => {
            const inactive = share.revokedAt || share.isExpired
            return (
              <li key={share.shareId}>
                <span>{share.permission === 'view' ? 'Chỉ xem' : 'Được sửa nội dung'} · hết hạn {new Date(share.expiresAt).toLocaleString('vi-VN')}</span>
                {inactive
                  ? <span className="share-inactive">{share.revokedAt ? 'Đã thu hồi' : 'Đã hết hạn'}</span>
                  : <button type="button" className="delete-action" disabled={busy} onClick={() => revokeShare(share)}>Thu hồi</button>}
              </li>
            )
          })}
        </ul>
      )}
      <p className="share-help">Link chỉ xuất hiện một lần khi tạo. Link sửa yêu cầu người nhận đăng nhập; mọi link có thể thu hồi tại đây.</p>
    </section>
  )
}

function SharedNotePage() {
  const token = window.location.pathname.slice('/share/'.length)
  const [note, setNote] = useState(null)
  const [attachments, setAttachments] = useState([])
  const [authenticated, setAuthenticated] = useState(false)
  const [title, setTitle] = useState('')
  const [content, setContent] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let active = true
    Promise.all([
      request(`/api/shared/${encodeURIComponent(token)}`),
      request('/api/auth/me').catch(() => null),
    ]).then(([sharedNote, account]) => {
      if (!active) return
      setNote(sharedNote)
      setTitle(sharedNote.title)
      setContent(sharedNote.content)
      setAuthenticated(Boolean(account))
    }).catch(() => {
      if (active) setError('Link không hợp lệ, đã hết hạn hoặc đã bị thu hồi.')
    })
    return () => {
      active = false
    }
  }, [token])

  useEffect(() => {
    let active = true
    request(`/api/shared/${encodeURIComponent(token)}/attachments`)
      .then((result) => {
        if (active) setAttachments(result)
      })
      .catch(() => {
        if (active) setAttachments([])
      })
    return () => {
      active = false
    }
  }, [token])

  async function saveSharedNote(event) {
    event.preventDefault()
    setBusy(true)
    setError('')
    try {
      const updated = await request(`/api/shared/${encodeURIComponent(token)}/update`, {
        method: 'POST',
        body: JSON.stringify({ title, content }),
      })
      setNote(updated)
    } catch (saveError) {
      setError(saveError.message)
    } finally {
      setBusy(false)
    }
  }

  if (error && !note) {
    return (
      <main className="shared-page">
        <section className="shared-card">
          <div className="brand-mark" aria-hidden="true">S</div>
          <h1>Không thể mở ghi chú</h1>
          <p role="alert">{error}</p>
          <a href="/">Về SecureNotes</a>
        </section>
      </main>
    )
  }

  if (!note) return <div className="loading-screen">Đang mở ghi chú được chia sẻ…</div>

  const canEdit = note.permission === 'edit' && authenticated
  return (
    <main className="shared-page">
      <article className="shared-card">
        <a className="shared-brand" href="/">SecureNotes</a>
        <p className="eyebrow">{note.permission === 'edit' ? 'Ghi chú được chia sẻ · quyền sửa' : 'Ghi chú được chia sẻ · chỉ xem'}</p>
        {canEdit ? (
          <form className="shared-edit-form" onSubmit={saveSharedNote}>
            <label htmlFor="shared-title">Tiêu đề</label>
            <input id="shared-title" required maxLength={200} value={title} onChange={(event) => setTitle(event.target.value)} />
            <label htmlFor="shared-content">Nội dung</label>
            <textarea id="shared-content" maxLength={20000} rows={12} value={content}
              onChange={(event) => setContent(event.target.value)} />
            <button className="primary-button" disabled={busy}>{busy ? 'Đang lưu…' : 'Lưu nội dung'}</button>
          </form>
        ) : (
          <>
            <h1>{note.title}</h1>
            <p className="shared-content">{note.content || 'Ghi chú này chưa có nội dung.'}</p>
            {note.permission === 'edit' && (
              <p className="share-login-notice">Đăng nhập để sửa nội dung của ghi chú này. <a href="/">Đăng nhập</a>, sau đó quay lại link chia sẻ.</p>
            )}
          </>
        )}
        {error && <p className="notice-error" role="alert">{error}</p>}
        <section className="shared-attachments" aria-label="Tệp đính kèm">
          <h2>Tệp đính kèm</h2>
          {attachments.length === 0
            ? <p>Không có tệp đính kèm.</p>
            : <ul className="attachment-list">{attachments.map((attachment) => (
              <li key={attachment.attachmentId}>
                <a href={`/api/shared/${encodeURIComponent(token)}/attachments/${attachment.attachmentId}/download`}>{attachment.fileName}</a>
                <span>{formatBytes(attachment.sizeBytes)}</span>
              </li>
            ))}</ul>}
        </section>
        <p className="share-expiry">Link hết hạn {new Date(note.expiresAt).toLocaleString('vi-VN')}.</p>
      </article>
    </main>
  )
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`
}

function isSafeHttpUrl(value) {
  if (!value) return false
  try {
    return ['http:', 'https:'].includes(new URL(value).protocol)
  } catch {
    return false
  }
}

function TwoFactorSettings({ onError }) {
  const [enabled, setEnabled] = useState(false)
  const [enrollment, setEnrollment] = useState(null)
  const [code, setCode] = useState('')
  const [recoveryCodes, setRecoveryCodes] = useState([])
  const [status, setStatus] = useState('')
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(async () => {
    const result = await request('/api/auth/2fa')
    setEnabled(result.enabled)
  }, [])

  useEffect(() => {
    refresh().catch((loadError) => onError(loadError.message))
  }, [onError, refresh])

  async function beginEnrollment() {
    setBusy(true)
    setStatus('')
    try {
      setEnrollment(await request('/api/auth/2fa/enroll', { method: 'POST' }))
    } catch (enrollError) {
      onError(enrollError.message)
    } finally {
      setBusy(false)
    }
  }

  async function enable(event) {
    event.preventDefault()
    setBusy(true)
    setStatus('')
    try {
      const result = await request('/api/auth/2fa/enable', {
        method: 'POST',
        body: JSON.stringify({ code }),
      })
      setRecoveryCodes(result.recoveryCodes)
      setEnabled(true)
      setEnrollment(null)
      setCode('')
      setStatus('Lưu các mã dự phòng ở nơi an toàn. Chúng chỉ được hiển thị lần này.')
    } catch (enableError) {
      onError(enableError.message)
    } finally {
      setBusy(false)
    }
  }

  async function disable(event) {
    event.preventDefault()
    setBusy(true)
    try {
      await request('/api/auth/2fa/disable', {
        method: 'POST',
        body: JSON.stringify({ code }),
      })
      setEnabled(false)
      setRecoveryCodes([])
      setCode('')
      setStatus('Đã tắt xác minh hai bước.')
    } catch (disableError) {
      onError(disableError.message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <details className="security-settings">
      <summary>Xác minh hai bước</summary>
      <p>{enabled ? 'Đang bật bằng ứng dụng TOTP.' : 'Chưa bật.'}</p>
      {status && <p role="status">{status}</p>}
      {recoveryCodes.length > 0 && (
        <div className="recovery-codes">
          <p>Mã dự phòng dùng một lần:</p>
          <ul>{recoveryCodes.map((item) => <li key={item}><code>{item}</code></li>)}</ul>
        </div>
      )}
      {!enabled && !enrollment && (
        <button className="secondary-button" disabled={busy} onClick={beginEnrollment}>
          Thiết lập ứng dụng xác thực
        </button>
      )}
      {enrollment && (
        <form className="security-form" onSubmit={enable}>
          <p>Thêm khóa này vào ứng dụng xác thực:</p>
          <code className="totp-secret">{enrollment.secret}</code>
          <p className="totp-uri">{enrollment.authenticatorUri}</p>
          <label htmlFor="totp-enable-code">Mã xác minh 6 chữ số</label>
          <input id="totp-enable-code" inputMode="numeric" autoComplete="one-time-code"
            minLength={6} maxLength={6} required value={code}
            onChange={(event) => setCode(event.target.value)} />
          <button className="secondary-button" disabled={busy}>Bật xác minh</button>
        </form>
      )}
      {enabled && (
        <form className="security-form" onSubmit={disable}>
          <label htmlFor="totp-disable-code">Mã xác minh để tắt</label>
          <input id="totp-disable-code" autoComplete="one-time-code" minLength={6} maxLength={32}
            required value={code} onChange={(event) => setCode(event.target.value)} />
          <button className="secondary-button" disabled={busy}>Tắt xác minh</button>
        </form>
      )}
    </details>
  )
}

function AdminPanel({ onError }) {
  const [users, setUsers] = useState([])
  const [auditEntries, setAuditEntries] = useState([])
  const [shares, setShares] = useState([])
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    const [userRows, auditRows, shareRows] = await Promise.all([
      request('/api/console-4d91/users'),
      request('/api/console-4d91/audit'),
      request('/api/console-4d91/shares'),
    ])
    setUsers(userRows)
    setAuditEntries(auditRows)
    setShares(shareRows)
  }, [])

  useEffect(() => {
    load().catch((loadError) => onError(loadError.message))
  }, [load, onError])

  async function act(path, description) {
    setBusy(true)
    try {
      await request(path, { method: 'POST' })
      await load()
    } catch (actionError) {
      onError(actionError.message)
    } finally {
      setBusy(false)
    }
    if (description) onError('')
  }

  return (
    <section className="admin-panel" aria-label="Quản trị hệ thống">
      <section>
        <h2>Tài khoản</h2>
        <div className="admin-table-wrap">
          <table className="admin-table">
            <thead><tr><th>Email</th><th>Vai trò</th><th>Trạng thái</th><th>2FA</th><th>Thao tác</th></tr></thead>
            <tbody>{users.map((user) => (
              <tr key={user.userId}>
                <td>{user.email}</td><td>{user.role}</td>
                <td>{user.isDisabled ? 'Đã khóa' : 'Hoạt động'}</td>
                <td>{user.twoFactorEnabled ? 'Bật' : 'Tắt'}</td>
                <td className="admin-actions">
                  <button disabled={busy} onClick={() => act(
                    `/api/console-4d91/users/${user.userId}/${user.isDisabled ? 'unlock' : 'lock'}`,
                    'user')}>{user.isDisabled ? 'Mở khóa' : 'Khóa'}</button>
                  <button disabled={busy || user.isDisabled} onClick={() => act(
                    `/api/console-4d91/users/${user.userId}/sessions/revoke`,
                    'sessions')}>Buộc đăng xuất</button>
                </td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      </section>
      <section>
        <h2>Link chia sẻ</h2>
        <div className="admin-table-wrap">
          <table className="admin-table">
            <thead><tr><th>Chủ sở hữu</th><th>Ghi chú ID</th><th>Quyền</th><th>Hết hạn</th><th>Trạng thái</th><th></th></tr></thead>
            <tbody>{shares.map((share) => (
              <tr key={share.shareId}>
                <td>{share.email}</td><td><code>{share.noteId}</code></td><td>{share.permission}</td>
                <td>{new Date(share.expiresAt).toLocaleString('vi-VN')}</td>
                <td>{share.revokedAt ? 'Đã thu hồi' : 'Đang hoạt động'}</td>
                <td>{!share.revokedAt && <button disabled={busy}
                  onClick={() => act(`/api/console-4d91/shares/${share.shareId}/revoke`, 'share')}>Thu hồi</button>}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      </section>
      <section>
        <h2>Nhật ký kiểm toán</h2>
        <div className="admin-table-wrap">
          <table className="admin-table">
            <thead><tr><th>Thời gian</th><th>Tài khoản</th><th>Hành động</th><th>IP</th><th>User-Agent</th></tr></thead>
            <tbody>{auditEntries.map((entry) => (
              <tr key={entry.auditLogId}>
                <td>{new Date(entry.createdAt).toLocaleString('vi-VN')}</td>
                <td>{entry.email ?? entry.userId ?? '—'}</td><td>{entry.action}</td>
                <td>{entry.ipAddress ?? '—'}</td><td>{entry.userAgent ?? '—'}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      </section>
    </section>
  )
}

function WorkspaceApp() {
  const [account, setAccount] = useState(null)
  const [authLoading, setAuthLoading] = useState(true)
  const [authMode, setAuthMode] = useState('login')
  const [authError, setAuthError] = useState(() => (
    googleAuthErrors[new URLSearchParams(window.location.search).get('authError')] ?? ''
  ))
  const [authStatus, setAuthStatus] = useState(() => (
    googleAuthStatuses[new URLSearchParams(window.location.search).get('authStatus')] ?? ''
  ))
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [linkCsrfToken, setLinkCsrfToken] = useState('')
  const [linkPassword, setLinkPassword] = useState('')
  const [notes, setNotes] = useState([])
  const [categories, setCategories] = useState([])
  const [categoryId, setCategoryId] = useState('')
  const [search, setSearch] = useState('')
  const [editor, setEditor] = useState(null)
  const [categoryName, setCategoryName] = useState('')
  const [expandedNote, setExpandedNote] = useState(null)
  const [items, setItems] = useState([])
  const [itemTitle, setItemTitle] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [twoFactorPending, setTwoFactorPending] = useState(
    () => new URLSearchParams(window.location.search).get('authStatus') === 'two_factor_required',
  )
  const [twoFactorCode, setTwoFactorCode] = useState('')
  const [adminMode, setAdminMode] = useState(false)

  useEffect(() => {
    const url = new URL(window.location.href)
    if (!url.searchParams.has('authError') && !url.searchParams.has('authStatus')) return
    url.searchParams.delete('authError')
    url.searchParams.delete('authStatus')
    window.history.replaceState(null, '', `${url.pathname}${url.search}${url.hash}`)
  }, [])

  const loadWorkspace = useCallback(async () => {
    const [noteRows, categoryRows] = await Promise.all([
      request(`/api/notes?search=${encodeURIComponent(search)}${categoryId ? `&categoryId=${categoryId}` : ''}`),
      request('/api/categories'),
    ])
    setNotes(noteRows)
    setCategories(categoryRows)
  }, [categoryId, search])

  useEffect(() => {
    let active = true
    request('/api/auth/me')
      .then((me) => {
        if (active) setAccount(me)
      })
      .catch(() => {
        if (active) {
          setAccount(null)
          setAuthError('Không thể kết nối tới API. Hãy kiểm tra Docker Compose và thử tải lại trang.')
        }
      })
      .finally(() => {
        if (active) setAuthLoading(false)
      })
    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    if (!account) return
    loadWorkspace().catch((loadError) => setError(loadError.message))
  }, [account, loadWorkspace])

  const visibleNotes = useMemo(
    () => notes.filter((note) => !note.parentId || search),
    [notes, search],
  )

  async function submitAuth(event) {
    event.preventDefault()
    setBusy(true)
    setAuthError('')
    try {
      const path = authMode === 'register' ? '/api/auth/register' : '/api/auth/login'
      const me = await request(path, {
        method: 'POST',
        body: JSON.stringify({ email, password }),
      })
      if (me.requiresTwoFactor) {
        setTwoFactorPending(true)
        setPassword('')
        setAuthStatus('two_factor_required')
        return
      }
      setAccount(me)
      setPassword('')
      setAuthStatus('')
    } catch (submitError) {
      setAuthError(submitError.message)
    } finally {
      setBusy(false)
    }
  }

  async function logout() {
    setError('')
    try {
      await request('/api/auth/logout', { method: 'POST' })
      setAccount(null)
      setAdminMode(false)
      setNotes([])
      setCategories([])
    } catch (logoutError) {
      setError(logoutError.message)
    }
  }

  async function submitTwoFactor(event) {
    event.preventDefault()
    setBusy(true)
    setAuthError('')
    try {
      const me = await request('/api/auth/2fa/verify', {
        method: 'POST',
        body: JSON.stringify({ code: twoFactorCode }),
      })
      setAccount(me)
      setTwoFactorPending(false)
      setTwoFactorCode('')
      setAuthStatus('')
    } catch (verifyError) {
      setAuthError(verifyError.message)
    } finally {
      setBusy(false)
    }
  }

  async function saveNote(event) {
    event.preventDefault()
    setBusy(true)
    setError('')
    const value = {
      ...editor,
      categoryId: editor.categoryId || null,
      parentId: editor.parentId || null,
      sourceUrl: editor.sourceUrl || null,
    }
    try {
      await request(editor.noteId ? `/api/notes/${editor.noteId}/update` : '/api/notes', {
        method: 'POST',
        body: JSON.stringify(value),
      })
      setEditor(null)
      await loadWorkspace()
    } catch (saveError) {
      setError(saveError.message)
    } finally {
      setBusy(false)
    }
  }

  async function removeNote(note) {
    if (!window.confirm(`Xóa ghi chú “${note.title}”?`)) return
    try {
      await request(`/api/notes/${note.noteId}/delete`, { method: 'POST' })
      await loadWorkspace()
    } catch (removeError) {
      setError(removeError.message)
    }
  }

  async function togglePin(note) {
    try {
      await request(`/api/notes/${note.noteId}/update`, {
        method: 'POST',
        body: JSON.stringify({ ...note, isPinned: !note.isPinned }),
      })
      await loadWorkspace()
    } catch (updateError) {
      setError(updateError.message)
    }
  }

  async function addCategory(event) {
    event.preventDefault()
    if (!categoryName.trim()) return
    try {
      await request('/api/categories', {
        method: 'POST',
        body: JSON.stringify({ name: categoryName }),
      })
      setCategoryName('')
      await loadWorkspace()
    } catch (categoryError) {
      setError(categoryError.message)
    }
  }

  async function removeCategory(category) {
    if (!window.confirm(`Xóa danh mục “${category.name}”?`)) return
    try {
      await request(`/api/categories/${category.categoryId}/delete`, { method: 'POST' })
      if (categoryId === category.categoryId) setCategoryId('')
      await loadWorkspace()
    } catch (categoryError) {
      setError(categoryError.message)
    }
  }

  async function renameCategory(category) {
    const name = window.prompt('Tên danh mục mới', category.name)
    if (name === null || !name.trim()) return
    try {
      await request(`/api/categories/${category.categoryId}/update`, {
        method: 'POST',
        body: JSON.stringify({ name }),
      })
      await loadWorkspace()
    } catch (categoryError) {
      setError(categoryError.message)
    }
  }

  async function openItems(note) {
    if (expandedNote === note.noteId) {
      setExpandedNote(null)
      return
    }
    try {
      const rows = await request(`/api/notes/${note.noteId}/items`)
      setItems(rows)
      setExpandedNote(note.noteId)
    } catch (itemsError) {
      setError(itemsError.message)
    }
  }

  async function addItem(noteId, event) {
    event.preventDefault()
    if (!itemTitle.trim()) return
    try {
      const item = await request(`/api/notes/${noteId}/items`, {
        method: 'POST',
        body: JSON.stringify({ title: itemTitle, isCompleted: false }),
      })
      setItems((current) => [...current, item])
      setItemTitle('')
      setNotes((current) => current.map((note) => note.noteId === noteId
        ? { ...note, itemCount: note.itemCount + 1 }
        : note))
    } catch (itemsError) {
      setError(itemsError.message)
    }
  }

  async function updateItem(item, changes) {
    try {
      const updated = await request(`/api/notes/${item.noteId}/items/${item.noteItemId}/update`, {
        method: 'POST',
        body: JSON.stringify({ title: changes.title ?? item.title, isCompleted: changes.isCompleted ?? item.isCompleted }),
      })
      setItems((current) => current.map((candidate) => candidate.noteItemId === item.noteItemId ? updated : candidate))
    } catch (itemsError) {
      setError(itemsError.message)
    }
  }

  async function deleteItem(item) {
    try {
      await request(`/api/notes/${item.noteId}/items/${item.noteItemId}/delete`, { method: 'POST' })
      setItems((current) => current.filter((candidate) => candidate.noteItemId !== item.noteItemId))
      setNotes((current) => current.map((note) => note.noteId === item.noteId
        ? { ...note, itemCount: Math.max(0, note.itemCount - 1) }
        : note))
    } catch (itemsError) {
      setError(itemsError.message)
    }
  }

  async function dropNote(targetId) {
    const draggedId = window.sessionStorage.getItem('securenotes-dragged')
    if (!draggedId || draggedId === targetId) return
    const ordered = [...visibleNotes]
    const from = ordered.findIndex((note) => note.noteId === draggedId)
    const to = ordered.findIndex((note) => note.noteId === targetId)
    if (from < 0 || to < 0) return
    const [moved] = ordered.splice(from, 1)
    ordered.splice(to, 0, moved)
    setNotes((current) => {
      const next = new Map(ordered.map((note, index) => [note.noteId, { ...note, position: index }]))
      return current.map((note) => next.get(note.noteId) ?? note)
    })
    window.sessionStorage.removeItem('securenotes-dragged')
    try {
      await Promise.all(ordered.map((note, position) => request(`/api/notes/${note.noteId}/update`, {
        method: 'POST',
        body: JSON.stringify({ ...note, position }),
      })))
    } catch (dragError) {
      setError(dragError.message)
      await loadWorkspace()
    }
  }

  if (authLoading) {
    return <main className="loading-screen" role="status">Đang tải SecureNotes…</main>
  }

  if (!account) {
    return (
      <main className="auth-page">
        <section className="auth-card">
          <div className="brand-mark" aria-hidden="true">S</div>
          <p className="eyebrow">Không gian riêng tư</p>
          <h1>{twoFactorPending ? 'Xác minh hai bước' : authMode === 'login' ? 'Chào mừng trở lại' : 'Tạo tài khoản'}</h1>
          <p className="auth-copy">{twoFactorPending
            ? 'Nhập mã 6 chữ số trong ứng dụng xác thực hoặc một mã dự phòng.'
            : 'Ghi lại ý tưởng, sắp xếp chúng theo cách của bạn.'}</p>
          {twoFactorPending ? (
            <form onSubmit={submitTwoFactor} className="auth-form">
              <label htmlFor="two-factor-code">Mã xác minh hoặc mã dự phòng</label>
              <input id="two-factor-code" autoComplete="one-time-code" autoFocus required maxLength={32}
                value={twoFactorCode} onChange={(event) => setTwoFactorCode(event.target.value)} />
              {authError && <p className="form-error" role="alert">{authError}</p>}
              <button className="primary-button auth-submit" disabled={busy}>
                {busy ? 'Đang xác minh…' : 'Xác minh'}
              </button>
              <button type="button" className="text-button" onClick={() => {
                setTwoFactorPending(false)
                setTwoFactorCode('')
                setAuthError('')
                setAuthStatus('')
              }}>Hủy và đăng nhập lại</button>
            </form>
          ) : (
            <>
              <form onSubmit={submitAuth} className="auth-form">
                <label htmlFor="email">Email</label>
                <input id="email" type="email" autoComplete="email" required maxLength={320}
                  value={email} onChange={(event) => setEmail(event.target.value)} />
                <label htmlFor="password">Mật khẩu</label>
                <input id="password" type="password" autoComplete="off"
                  required minLength={authMode === 'register' ? 12 : 1} maxLength={128}
                  pattern={authMode === 'register' ? '(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[^A-Za-z0-9]).{12,128}' : undefined}
                  title={authMode === 'register' ? 'Cần ít nhất 12 ký tự, gồm chữ hoa, chữ thường, số và ký tự đặc biệt.' : undefined}
                  value={password} onChange={(event) => setPassword(event.target.value)} />
                {authError && <p className="form-error" role="alert">{authError}</p>}
                <button className="primary-button auth-submit" disabled={busy}>
                  {busy ? 'Đang xử lý…' : authMode === 'login' ? 'Đăng nhập' : 'Đăng ký'}
                </button>
              </form>
              {authMode === 'login' && (
                <>
                  <div className="auth-divider"><span>hoặc</span></div>
                  <a className="google-login-button" href="/api/auth/google?returnUrl=%2F">
                    Tiếp tục với Google
                  </a>
                </>
              )}
              <p className="auth-switch">
                {authMode === 'login' ? 'Chưa có tài khoản?' : 'Đã có tài khoản?'}{' '}
                <button className="text-button" onClick={() => {
                  setAuthMode(authMode === 'login' ? 'register' : 'login')
                  setAuthError('')
                }}>{authMode === 'login' ? 'Đăng ký' : 'Đăng nhập'}</button>
              </p>
            </>
          )}
          {authStatus && <p className="notice-success" role="status">{googleAuthStatuses[authStatus] ?? ''}</p>}
        </section>
      </main>
    )
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <a className="brand" href="/" aria-label="SecureNotes trang chủ">
          <span className="brand-mark">S</span><span>SecureNotes</span>
        </a>
        <div className="sidebar-section">
          <p className="sidebar-label">Không gian</p>
          <button className={`nav-item ${!categoryId ? 'active' : ''}`} onClick={() => setCategoryId('')}>
            <span aria-hidden="true">▦</span> Tất cả ghi chú <span className="nav-count">{notes.length}</span>
          </button>
        </div>
        <div className="sidebar-section categories-section">
          <div className="sidebar-heading">
            <p className="sidebar-label">Danh mục</p>
            <button className="icon-button sidebar-add" aria-label="Thêm danh mục"
              onClick={() => document.getElementById('new-category')?.focus()}>+</button>
          </div>
          {categories.map((category) => (
            <div className="category-row" key={category.categoryId}>
              <button className={`nav-item ${categoryId === category.categoryId ? 'active' : ''}`}
                onClick={() => setCategoryId(category.categoryId)}>
                <span className="category-dot" aria-hidden="true" />{category.name}
                <span className="nav-count">{category.noteCount}</span>
              </button>
              <button className="icon-button category-edit" aria-label={`Đổi tên ${category.name}`}
                onClick={() => renameCategory(category)}>✎</button>
              <button className="icon-button category-delete" aria-label={`Xóa ${category.name}`}
                onClick={() => removeCategory(category)}>×</button>
            </div>
          ))}
          <form className="category-form" onSubmit={addCategory}>
            <input id="new-category" aria-label="Tên danh mục mới" placeholder="+ Danh mục mới"
              maxLength={100} value={categoryName} onChange={(event) => setCategoryName(event.target.value)} />
            {categoryName.trim() && <button className="icon-button" aria-label="Tạo danh mục">↵</button>}
          </form>
        </div>
        <div className="sidebar-bottom">
          <TwoFactorSettings onError={setError} />
          <details className="google-link-settings" onToggle={(event) => {
            if (!event.currentTarget.open || linkCsrfToken) return
            request('/api/csrf')
              .then(({ requestToken }) => setLinkCsrfToken(requestToken))
              .catch((csrfError) => setError(csrfError.message))
          }}>
            <summary>Cài đặt liên kết</summary>
            <form method="post" action="/api/auth/google/link">
              <input type="hidden" name="__RequestVerificationToken" value={linkCsrfToken} />
              <label htmlFor="google-link-password">Xác nhận mật khẩu</label>
              <input id="google-link-password" name="Password" type="password" autoComplete="current-password"
                required maxLength={128} value={linkPassword}
                onChange={(event) => setLinkPassword(event.target.value)} />
              <button className="secondary-button" disabled={!linkCsrfToken}>Liên kết Google</button>
            </form>
          </details>
          <span className="avatar">{account.email[0]?.toUpperCase()}</span>
          <span className="account-email" title={account.email}>{account.email}</span>
          <button className="icon-button logout-button" onClick={logout} aria-label="Đăng xuất" title="Đăng xuất">↗</button>
        </div>
      </aside>

      <main className="workspace">
        <header className="topbar">
          <div>
            <p className="eyebrow">{adminMode ? 'Quản trị hệ thống' : 'Ghi chú của bạn'}</p>
            <h1>{adminMode ? 'Bảng quản trị' : categoryId ? categories.find((category) => category.categoryId === categoryId)?.name : 'Notes Manager'}</h1>
          </div>
          {!adminMode && <button className="primary-button create-button" onClick={() => setEditor(blankNote(categoryId))}>
            <span aria-hidden="true">+</span> Ghi chú mới
          </button>}
        </header>

        {!adminMode && <section className="toolbar" aria-label="Tìm và lọc ghi chú">
          <label className="search-box">
            <span aria-hidden="true">⌕</span>
            <input type="search" placeholder="Tìm kiếm ghi chú…" value={search}
              onChange={(event) => setSearch(event.target.value)} aria-label="Tìm kiếm ghi chú" />
            <kbd>⌘ K</kbd>
          </label>
          <span className="result-count">{visibleNotes.length} ghi chú</span>
        </section>}

        {error && <div className="notice-error" role="alert">{error}<button onClick={() => setError('')} aria-label="Đóng">×</button></div>}
        {!adminMode && authError && <div className="notice-error" role="alert">{authError}</div>}
        {!adminMode && authStatus && <div className="notice-success" role="status">{googleAuthStatuses[authStatus] ?? ''}</div>}

        {adminMode ? (
          <AdminPanel onError={setError} />
        ) : visibleNotes.length === 0 ? (
          <section className="empty-state">
            <div className="empty-icon" aria-hidden="true">✦</div>
            <h2>{search ? 'Không tìm thấy ghi chú' : 'Bắt đầu với ghi chú đầu tiên'}</h2>
            <p>{search ? 'Thử từ khóa khác hoặc bỏ bộ lọc danh mục.' : 'Lưu nhanh một ý tưởng để bạn không bỏ lỡ.'}</p>
            {!search && <button className="primary-button" onClick={() => setEditor(blankNote(categoryId))}>Tạo ghi chú</button>}
          </section>
        ) : (
          <section className="notes-grid" aria-label="Danh sách ghi chú">
            {visibleNotes.map((note) => (
              <article className={`note-card ${note.isPinned ? 'pinned' : ''}`} key={note.noteId}
                style={{ '--note-color': note.color }} draggable={!search && !categoryId}
                onDragStart={() => {
                  if (!search && !categoryId) window.sessionStorage.setItem('securenotes-dragged', note.noteId)
                }}
                onDragOver={(event) => {
                  if (!search && !categoryId) event.preventDefault()
                }}
                onDrop={() => {
                  if (!search && !categoryId) dropNote(note.noteId)
                }}>
                <div className="note-card-top">
                  <span className="note-category">{note.categoryName ?? 'Chưa phân loại'}</span>
                  <button className={`icon-button pin-button ${note.isPinned ? 'is-pinned' : ''}`}
                    onClick={() => togglePin(note)} aria-label={note.isPinned ? 'Bỏ ghim' : 'Ghim ghi chú'}
                    title={note.isPinned ? 'Bỏ ghim' : 'Ghim'}>⌑</button>
                </div>
                <button className="note-content-button" onClick={() => openItems(note)} aria-label="Mở ghi chú">
                  <h2>{note.title}</h2>
                  <p className="note-excerpt">{note.content || 'Chưa có nội dung.'}</p>
                </button>
                {isSafeHttpUrl(note.sourceUrl) && (
                  <a className="source-link" href={note.sourceUrl} target="_blank" rel="noopener noreferrer">
                    ↗ {new URL(note.sourceUrl).hostname}
                  </a>
                )}
                <div className="note-meta">
                  <span className="importance" aria-label={`Mức độ ưu tiên ${note.importance}`}>
                    {'●'.repeat(note.importance)}<span>{'○'.repeat(4 - note.importance)}</span>
                  </span>
                  <span>{new Date(note.updatedAt).toLocaleDateString('vi-VN')}</span>
                </div>
                <button className="items-toggle" onClick={() => openItems(note)}>
                  ☑ {note.itemCount > 0 ? `${note.itemCount} mục` : 'Thêm mục con'} {expandedNote === note.noteId ? '▴' : '▾'}
                </button>
                {expandedNote === note.noteId && (
                  <div className="items-panel">
                    <AttachmentPanel noteId={note.noteId} onError={setError} />
                    <SharePanel noteId={note.noteId} onError={setError} />
                    {notes.filter((child) => child.parentId === note.noteId).map((child) => (
                      <div className="subnote-row" key={child.noteId}>
                        <button className="subnote-title" onClick={() => setEditor({ ...child })}>
                          ↳ {child.title}
                        </button>
                        <button className="icon-button" onClick={() => removeNote(child)} aria-label={`Xóa ${child.title}`}>×</button>
                      </div>
                    ))}
                    <button className="subnote-add" onClick={() => setEditor({
                      ...blankNote(note.categoryId ?? ''),
                      parentId: note.noteId,
                    })}>＋ Ghi chú con</button>
                    {items.map((item) => (
                      <div className="item-row" key={item.noteItemId}>
                        <input type="checkbox" checked={item.isCompleted} onChange={(event) => updateItem(item, { isCompleted: event.target.checked })}
                          aria-label={`Hoàn thành: ${item.title}`} />
                        <span className={item.isCompleted ? 'item-complete' : ''}>{item.title}</span>
                        <button className="icon-button" onClick={() => deleteItem(item)} aria-label={`Xóa ${item.title}`}>×</button>
                      </div>
                    ))}
                    <form className="item-add-form" onSubmit={(event) => addItem(note.noteId, event)}>
                      <input value={itemTitle} maxLength={1000} onChange={(event) => setItemTitle(event.target.value)}
                        placeholder="Thêm mục con…" aria-label="Mục con mới" />
                      <button className="icon-button" aria-label="Thêm mục">+</button>
                    </form>
                  </div>
                )}
                <div className="note-actions">
                  <button onClick={() => setEditor({ ...note })}>Chỉnh sửa</button>
                  <button className="delete-action" onClick={() => removeNote(note)}>Xóa</button>
                </div>
              </article>
            ))}
          </section>
        )}
      </main>

      {editor && (
        <div className="modal-backdrop" role="presentation" onMouseDown={(event) => {
          if (event.target === event.currentTarget) setEditor(null)
        }}>
          <section className="note-editor" role="dialog" aria-modal="true" aria-labelledby="editor-title">
            <div className="editor-heading">
              <div><p className="eyebrow">{editor.noteId ? 'Chỉnh sửa' : 'Ghi chú mới'}</p><h2 id="editor-title">{editor.noteId ? 'Cập nhật ghi chú' : 'Tạo ghi chú'}</h2></div>
              <button className="icon-button" onClick={() => setEditor(null)} aria-label="Đóng">×</button>
            </div>
            <form onSubmit={saveNote}>
              <label htmlFor="note-title">Tiêu đề</label>
              <input id="note-title" required maxLength={200} value={editor.title}
                onChange={(event) => setEditor({ ...editor, title: event.target.value })} autoFocus />
              <label htmlFor="note-content">Nội dung</label>
              <textarea id="note-content" maxLength={20000} rows={7} value={editor.content}
                onChange={(event) => setEditor({ ...editor, content: event.target.value })} />
              <div className="editor-columns">
                <div><label htmlFor="note-category">Danh mục</label>
                  <select id="note-category" value={editor.categoryId ?? ''}
                    onChange={(event) => setEditor({ ...editor, categoryId: event.target.value })}>
                    <option value="">Chưa phân loại</option>
                    {categories.map((category) => <option key={category.categoryId} value={category.categoryId}>{category.name}</option>)}
                  </select>
                </div>
                <div><label htmlFor="note-importance">Ưu tiên</label>
                  <select id="note-importance" value={editor.importance}
                    onChange={(event) => setEditor({ ...editor, importance: Number(event.target.value) })}>
                    {[0, 1, 2, 3, 4].map((level) => <option key={level} value={level}>{level === 0 ? 'Không' : 'Mức ' + level}</option>)}
                  </select>
                </div>
              </div>
              <label htmlFor="note-url">URL nguồn (HTTP/HTTPS)</label>
              <input id="note-url" type="url" maxLength={2048} value={editor.sourceUrl ?? ''}
                onChange={(event) => setEditor({ ...editor, sourceUrl: event.target.value })} placeholder="https://" />
              <fieldset className="color-picker">
                <legend>Màu thẻ</legend>
                {colors.map((color) => (
                  <button type="button" className={`color-swatch ${editor.color === color ? 'selected' : ''}`}
                    key={color} style={{ backgroundColor: color }} onClick={() => setEditor({ ...editor, color })}
                    aria-label={`Chọn màu ${color}`} aria-pressed={editor.color === color} />
                ))}
              </fieldset>
              <label className="checkbox-label"><input type="checkbox" checked={editor.isPinned}
                onChange={(event) => setEditor({ ...editor, isPinned: event.target.checked })} /> Ghim ghi chú</label>
              <div className="editor-footer">
                <button type="button" className="secondary-button" onClick={() => setEditor(null)}>Hủy</button>
                <button className="primary-button" disabled={busy}>{busy ? 'Đang lưu…' : 'Lưu ghi chú'}</button>
              </div>
            </form>
          </section>
        </div>
      )}
    </div>
  )
}

function App() {
  if (window.location.pathname.startsWith('/share/')) {
    return <SharedNotePage />
  }

  return <WorkspaceApp />
}

function blankNote(categoryId = '') {
  return {
    title: '',
    content: '',
    categoryId,
    parentId: null,
    sourceUrl: '',
    color: colors[0],
    importance: 0,
    isPinned: false,
    position: 0,
  }
}

export default App
