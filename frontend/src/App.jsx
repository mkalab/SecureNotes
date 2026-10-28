import { useCallback, useEffect, useMemo, useState } from 'react'
import './App.css'

const colors = ['#fff8c5', '#fde2e2', '#dbeafe', '#dcfce7', '#f3e8ff', '#ffffff']
const googleAuthErrors = {
  google_unavailable: 'Đăng nhập Google chưa được cấu hình trên máy chủ này.',
  google_failed: 'Không thể hoàn tất đăng nhập Google. Hãy thử lại hoặc đăng nhập bằng email và mật khẩu.',
  google_link_failed: 'Không thể liên kết tài khoản Google. Hãy kiểm tra mật khẩu và thử lại.',
}
const googleAuthStatuses = {
  google_linked: 'Đã liên kết tài khoản Google. Lần sau bạn có thể đăng nhập bằng Google.',
}

async function request(path, options = {}) {
  const headers = new Headers(options.headers)
  if (options.body) headers.set('Content-Type', 'application/json')

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

function isSafeHttpUrl(value) {
  if (!value) return false
  try {
    return ['http:', 'https:'].includes(new URL(value).protocol)
  } catch {
    return false
  }
}

function App() {
  const [account, setAccount] = useState(null)
  const [authLoading, setAuthLoading] = useState(true)
  const [authMode, setAuthMode] = useState('login')
  const [authError, setAuthError] = useState(() => (
    googleAuthErrors[new URLSearchParams(window.location.search).get('authError')] ?? ''
  ))
  const [authStatus] = useState(() => (
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
      setAccount(me)
      setPassword('')
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
      setNotes([])
      setCategories([])
    } catch (logoutError) {
      setError(logoutError.message)
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
          <h1>{authMode === 'login' ? 'Chào mừng trở lại' : 'Tạo tài khoản'}</h1>
          <p className="auth-copy">Ghi lại ý tưởng, sắp xếp chúng theo cách của bạn.</p>
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
            <p className="eyebrow">Ghi chú của bạn</p>
            <h1>{categoryId ? categories.find((category) => category.categoryId === categoryId)?.name : 'Notes Manager'}</h1>
          </div>
          <button className="primary-button create-button" onClick={() => setEditor(blankNote(categoryId))}>
            <span aria-hidden="true">+</span> Ghi chú mới
          </button>
        </header>

        <section className="toolbar" aria-label="Tìm và lọc ghi chú">
          <label className="search-box">
            <span aria-hidden="true">⌕</span>
            <input type="search" placeholder="Tìm kiếm ghi chú…" value={search}
              onChange={(event) => setSearch(event.target.value)} aria-label="Tìm kiếm ghi chú" />
            <kbd>⌘ K</kbd>
          </label>
          <span className="result-count">{visibleNotes.length} ghi chú</span>
        </section>

        {error && <div className="notice-error" role="alert">{error}<button onClick={() => setError('')} aria-label="Đóng">×</button></div>}
        {authError && <div className="notice-error" role="alert">{authError}</div>}
        {authStatus && <div className="notice-success" role="status">{authStatus}</div>}

        {visibleNotes.length === 0 ? (
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
