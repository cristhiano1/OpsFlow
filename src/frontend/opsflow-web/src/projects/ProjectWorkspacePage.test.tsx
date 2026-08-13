import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, MemoryRouter, Route, RouterProvider, Routes } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('./documentsApi', () => ({
  listDocuments: vi.fn(),
  uploadDocument: vi.fn(),
  DocumentApiError: class DocumentApiError extends Error {
    status: number
    serverMessage: string | null
    constructor(status: number, serverMessage: string | null) {
      super(`Document API error ${status}`)
      this.name = 'DocumentApiError'
      this.status = status
      this.serverMessage = serverMessage
    }
  },
  ProjectNotFoundError: class ProjectNotFoundError extends Error {
    constructor() {
      super('Project not found')
      this.name = 'ProjectNotFoundError'
    }
  },
}))

import {
  listDocuments,
  uploadDocument,
  DocumentApiError,
  ProjectNotFoundError,
} from './documentsApi'
import { ProjectWorkspacePage } from './ProjectWorkspacePage'

const mockListDocuments = vi.mocked(listDocuments)
const mockUploadDocument = vi.mocked(uploadDocument)

const SAMPLE_DOC = {
  id: 'd1',
  originalFileName: 'report.pdf',
  contentType: 'application/pdf',
  sizeBytes: 102400,
  createdAt: '2026-08-01T00:00:00Z',
}

const WORKSPACE_ROUTES = [
  { path: '/projects/:projectId', element: <ProjectWorkspacePage /> },
]

function renderWorkspace(projectId = 'proj-1') {
  return render(
    <MemoryRouter initialEntries={[`/projects/${projectId}`]}>
      <Routes>
        <Route path="/projects/:projectId" element={<ProjectWorkspacePage />} />
      </Routes>
    </MemoryRouter>,
  )
}

function renderWithRouter(initialPath: string) {
  const router = createMemoryRouter(WORKSPACE_ROUTES, {
    initialEntries: [initialPath],
  })
  return { ...render(<RouterProvider router={router} />), router }
}

function makePdf(name = 'file.pdf', size = 1024) {
  return new File([new Uint8Array(size)], name, { type: 'application/pdf' })
}

function makeDocApiError(status: number, msg: string | null = null) {
  return new DocumentApiError(status, msg)
}

function makeNotFoundError() {
  return new ProjectNotFoundError()
}

beforeEach(() => {
  mockListDocuments.mockReset()
  mockUploadDocument.mockReset()
})

// ── Loading / error / empty / not-found ────────────────────────────────────

describe('Loading states', () => {
  it('shows loading state initially', () => {
    mockListDocuments.mockReturnValue(new Promise(() => {}))
    renderWorkspace()
    expect(screen.getByText('Loading documents…')).toBeInTheDocument()
  })

  it('shows empty state when project has no documents', async () => {
    mockListDocuments.mockResolvedValue({ items: [] })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument()
    })
  })

  it('shows document list when loaded', async () => {
    mockListDocuments.mockResolvedValue({ items: [SAMPLE_DOC] })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })
  })

  it('shows error state when list fails', async () => {
    mockListDocuments.mockRejectedValue(new Error('Network error'))
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('Network error')).toBeInTheDocument()
    })
  })

  it('shows project not found state on 404', async () => {
    mockListDocuments.mockRejectedValue(makeNotFoundError())
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('Project not found.')).toBeInTheDocument()
    })
  })

  it('retry button triggers a new document load', async () => {
    const user = userEvent.setup()
    mockListDocuments
      .mockRejectedValueOnce(new Error('Fail'))
      .mockResolvedValueOnce({ items: [SAMPLE_DOC] })

    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: 'Retry' }))

    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })
  })
})

// ── Document list rendering ─────────────────────────────────────────────────

describe('Document list rendering', () => {
  it('renders the original file name', async () => {
    mockListDocuments.mockResolvedValue({ items: [SAMPLE_DOC] })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })
  })

  it('renders PDF content type as "PDF"', async () => {
    mockListDocuments.mockResolvedValue({ items: [SAMPLE_DOC] })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('PDF')).toBeInTheDocument()
    })
  })

  it('renders text/plain as "Text"', async () => {
    mockListDocuments.mockResolvedValue({
      items: [{ ...SAMPLE_DOC, contentType: 'text/plain', originalFileName: 'notes.txt' }],
    })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('Text')).toBeInTheDocument()
    })
  })

  it('renders docx content type as "Word document"', async () => {
    mockListDocuments.mockResolvedValue({
      items: [{
        ...SAMPLE_DOC,
        contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        originalFileName: 'doc.docx',
      }],
    })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('Word document')).toBeInTheDocument()
    })
  })

  it('renders unknown content type as "Document" fallback', async () => {
    mockListDocuments.mockResolvedValue({
      items: [{ ...SAMPLE_DOC, contentType: 'application/octet-stream' }],
    })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('Document')).toBeInTheDocument()
    })
  })

  it('formats file size in human-readable form (KB)', async () => {
    mockListDocuments.mockResolvedValue({ items: [{ ...SAMPLE_DOC, sizeBytes: 102400 }] })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText(/102\.4 KB/)).toBeInTheDocument()
    })
  })

  it('formats file size in MB for large files', async () => {
    mockListDocuments.mockResolvedValue({ items: [{ ...SAMPLE_DOC, sizeBytes: 5_000_000 }] })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText(/5\.0 MB/)).toBeInTheDocument()
    })
  })

  it('formats file size in B for tiny files', async () => {
    mockListDocuments.mockResolvedValue({ items: [{ ...SAMPLE_DOC, sizeBytes: 512 }] })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText(/512 B/)).toBeInTheDocument()
    })
  })

  it('renders UTC date in YYYY-MM-DD format deterministically', async () => {
    mockListDocuments.mockResolvedValue({
      items: [{ ...SAMPLE_DOC, createdAt: '2026-08-01T23:59:59Z' }],
    })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText(/2026-08-01/)).toBeInTheDocument()
    })
  })

  it('renders documents using semantic list markup', async () => {
    mockListDocuments.mockResolvedValue({
      items: [SAMPLE_DOC, { ...SAMPLE_DOC, id: 'd2', originalFileName: 'other.txt' }],
    })
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getAllByRole('listitem')).toHaveLength(2)
    })
  })
})

// ── Upload form accessibility ──────────────────────────────────────────────

describe('Upload form accessibility', () => {
  it('renders file input with an associated label', async () => {
    mockListDocuments.mockResolvedValue({ items: [] })
    renderWorkspace()
    await waitFor(() =>
      expect(screen.queryByText('Loading documents…')).not.toBeInTheDocument(),
    )
    expect(screen.getByLabelText('Choose file')).toBeInTheDocument()
  })

  it('file input accept attribute includes pdf txt docx', async () => {
    mockListDocuments.mockResolvedValue({ items: [] })
    renderWorkspace()
    await waitFor(() =>
      expect(screen.queryByText('Loading documents…')).not.toBeInTheDocument(),
    )
    expect(screen.getByLabelText('Choose file')).toHaveAttribute('accept', '.pdf,.txt,.docx')
  })

  it('upload button is disabled when no file is selected', async () => {
    mockListDocuments.mockResolvedValue({ items: [] })
    renderWorkspace()
    await waitFor(() =>
      expect(screen.queryByText('Loading documents…')).not.toBeInTheDocument(),
    )
    expect(screen.getByRole('button', { name: 'Upload' })).toBeDisabled()
  })
})

// ── Client-side validation ─────────────────────────────────────────────────

describe('Client-side validation', () => {
  it('rejects zero-byte file before calling the API', async () => {
    const user = userEvent.setup()
    mockListDocuments.mockResolvedValue({ items: [] })
    renderWorkspace()
    await waitFor(() =>
      expect(screen.queryByText('Loading documents…')).not.toBeInTheDocument(),
    )

    const file = new File([], 'empty.pdf', { type: 'application/pdf' })
    await user.upload(screen.getByLabelText('Choose file'), file)
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    expect(mockUploadDocument).not.toHaveBeenCalled()
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('File is empty.')
    })
  })

  it('rejects file over 25 MiB before calling the API', async () => {
    const user = userEvent.setup()
    mockListDocuments.mockResolvedValue({ items: [] })
    renderWorkspace()
    await waitFor(() =>
      expect(screen.queryByText('Loading documents…')).not.toBeInTheDocument(),
    )

    const oversized = new File(
      [new Uint8Array(25 * 1024 * 1024 + 1)],
      'big.pdf',
      { type: 'application/pdf' },
    )
    await user.upload(screen.getByLabelText('Choose file'), oversized)
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    expect(mockUploadDocument).not.toHaveBeenCalled()
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('File exceeds the 25 MiB limit.')
    })
  })

  it('rejects unsupported extension before calling the API', async () => {
    // applyAccept: false bypasses userEvent's accept-filter so our JS validation actually runs
    const user = userEvent.setup({ applyAccept: false })
    mockListDocuments.mockResolvedValue({ items: [] })
    renderWorkspace()
    await waitFor(() =>
      expect(screen.queryByText('Loading documents…')).not.toBeInTheDocument(),
    )

    const file = new File(['data'], 'image.png', { type: 'image/png' })
    await user.upload(screen.getByLabelText('Choose file'), file)
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    expect(mockUploadDocument).not.toHaveBeenCalled()
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'Only PDF, TXT, and DOCX files are supported.',
      )
    })
  })

  it('accepts uppercase .PDF extension (case-insensitive)', async () => {
    const user = userEvent.setup()
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValue({ items: [SAMPLE_DOC] })
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.queryByText('Loading documents…')).not.toBeInTheDocument(),
    )

    const file = new File([new Uint8Array(100)], 'REPORT.PDF', { type: 'application/pdf' })
    await user.upload(screen.getByLabelText('Choose file'), file)
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(mockUploadDocument).toHaveBeenCalledTimes(1)
    })
  })

  it('accepts file of exactly 25 MiB (boundary)', async () => {
    const user = userEvent.setup()
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValue({ items: [SAMPLE_DOC] })
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.queryByText('Loading documents…')).not.toBeInTheDocument(),
    )

    const file = new File([new Uint8Array(1)], 'limit.pdf', { type: 'application/pdf' })
    Object.defineProperty(file, 'size', { value: 25 * 1024 * 1024 })
    await user.upload(screen.getByLabelText('Choose file'), file)
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(mockUploadDocument).toHaveBeenCalledTimes(1)
    })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

// ── Upload success flow ────────────────────────────────────────────────────

describe('Upload success flow', () => {
  it('PDF upload succeeds and refreshes the document list', async () => {
    const user = userEvent.setup()
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValueOnce({ items: [SAMPLE_DOC] })
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })
  })

  it('TXT upload succeeds and refreshes the document list', async () => {
    const user = userEvent.setup()
    const txtDoc = { ...SAMPLE_DOC, id: 'd2', originalFileName: 'notes.txt', contentType: 'text/plain' }
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValueOnce({ items: [txtDoc] })
    mockUploadDocument.mockResolvedValue(txtDoc)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    const file = new File([new Uint8Array(100)], 'notes.txt', { type: 'text/plain' })
    await user.upload(screen.getByLabelText('Choose file'), file)
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(screen.getByText('notes.txt')).toBeInTheDocument()
    })
  })

  it('DOCX upload succeeds and refreshes the document list', async () => {
    const user = userEvent.setup()
    const docxDoc = {
      ...SAMPLE_DOC,
      id: 'd3',
      originalFileName: 'doc.docx',
      contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    }
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValueOnce({ items: [docxDoc] })
    mockUploadDocument.mockResolvedValue(docxDoc)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    const file = new File([new Uint8Array(100)], 'doc.docx', {
      type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    })
    await user.upload(screen.getByLabelText('Choose file'), file)
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(screen.getByText('doc.docx')).toBeInTheDocument()
    })
  })

  it('successful upload triggers a GET documents reload', async () => {
    const user = userEvent.setup()
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValueOnce({ items: [SAMPLE_DOC] })
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(mockListDocuments).toHaveBeenCalledTimes(2)
    })
  })

  it('clears the file input after successful upload', async () => {
    const user = userEvent.setup()
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValueOnce({ items: [SAMPLE_DOC] })
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    const input = screen.getByLabelText('Choose file') as HTMLInputElement
    await user.upload(input, makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })
    expect(input.value).toBe('')
  })
})

// ── Uploading state ────────────────────────────────────────────────────────

describe('Uploading state', () => {
  it('disables upload button while upload is in progress', async () => {
    const user = userEvent.setup()
    mockListDocuments.mockResolvedValue({ items: [] })
    mockUploadDocument.mockReturnValue(new Promise(() => {}))
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    expect(screen.getByRole('button', { name: /Uploading/ })).toBeDisabled()
  })

  it('prevents duplicate submission while uploading', async () => {
    const user = userEvent.setup()
    mockListDocuments.mockResolvedValue({ items: [] })
    mockUploadDocument.mockReturnValue(new Promise(() => {}))
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))
    await user.click(screen.getByRole('button', { name: /Uploading/ }))

    expect(mockUploadDocument).toHaveBeenCalledTimes(1)
  })
})

// ── Upload error handling ─────────────────────────────────────────────────

describe('Upload error handling', () => {
  async function setupAndUpload(error: unknown) {
    const user = userEvent.setup()
    mockListDocuments.mockResolvedValue({ items: [] })
    mockUploadDocument.mockRejectedValue(error)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )
    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))
  }

  it('shows error message on 400 response', async () => {
    await setupAndUpload(makeDocApiError(400, 'A non-empty file is required.'))
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('A non-empty file is required.')
    })
  })

  it('shows 404 project not found error', async () => {
    await setupAndUpload(makeNotFoundError())
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('This project was not found.')
    })
  })

  it('shows server size limit message on 413 response', async () => {
    await setupAndUpload(makeDocApiError(413, null))
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('The file exceeds the server size limit.')
    })
  })

  it('shows unsupported type message on 422 response', async () => {
    await setupAndUpload(makeDocApiError(422, 'Unsupported file type.'))
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Unsupported file type.')
    })
  })

  it('shows generic message on unknown server error', async () => {
    await setupAndUpload(makeDocApiError(500, null))
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Upload failed. Please try again.')
    })
  })

  it('auth/session rejection does not corrupt page state', async () => {
    const user = userEvent.setup()
    mockListDocuments.mockResolvedValue({ items: [SAMPLE_DOC] })
    const SessionReplacedError = class extends Error {
      constructor() {
        super('Session replaced')
        this.name = 'SessionReplacedError'
      }
    }
    mockUploadDocument.mockRejectedValue(new SessionReplacedError())
    renderWorkspace()
    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument()
    })
    expect(screen.getByText('report.pdf')).toBeInTheDocument()
  })
})

// ── Upload success / refresh failure ─────────────────────────────────────

describe('Upload succeeds but refresh fails', () => {
  it('shows refresh-failed notice without claiming upload failed', async () => {
    const user = userEvent.setup()
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockRejectedValueOnce(new Error('Network error'))
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(
        screen.getByText(/Upload succeeded, but the document list could not be refreshed/),
      ).toBeInTheDocument()
    })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('retry after refresh-failed reloads the document list', async () => {
    const user = userEvent.setup()
    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockRejectedValueOnce(new Error('Fail'))
      .mockResolvedValueOnce({ items: [SAMPLE_DOC] })
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)
    renderWorkspace()
    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(screen.getByText(/Upload succeeded/)).toBeInTheDocument()
    })
    await user.click(screen.getByRole('button', { name: 'Retry' }))

    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })
  })
})

// ── Race safety: stale load guards ────────────────────────────────────────

describe('Race safety: stale load guards', () => {
  it('old list response cannot overwrite the post-upload new list', async () => {
    const user = userEvent.setup()

    type Resolve<T> = (value: T) => void
    function deferred<T>() {
      let resolve!: Resolve<T>
      const promise = new Promise<T>((r) => { resolve = r })
      return { promise, resolve }
    }

    const initialLoad = deferred<{ items: typeof SAMPLE_DOC[] }>()
    const postUploadLoad = deferred<{ items: typeof SAMPLE_DOC[] }>()

    mockListDocuments
      .mockReturnValueOnce(initialLoad.promise)
      .mockReturnValueOnce(postUploadLoad.promise)
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)

    renderWorkspace()
    expect(screen.getByText('Loading documents…')).toBeInTheDocument()

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    postUploadLoad.resolve({ items: [SAMPLE_DOC] })

    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })

    await act(async () => {
      initialLoad.resolve({ items: [] })
      await initialLoad.promise
    })

    expect(screen.getByText('report.pdf')).toBeInTheDocument()
    expect(screen.queryByText('No documents yet. Upload one above.')).not.toBeInTheDocument()
  })

  it('old list error cannot overwrite the new list', async () => {
    const user = userEvent.setup()

    type Reject = (reason: Error) => void
    function deferredReject<T>() {
      let reject!: Reject
      const promise = new Promise<T>((_, r) => { reject = r as Reject })
      return { promise, reject }
    }

    const initialLoad = deferredReject<{ items: typeof SAMPLE_DOC[] }>()

    mockListDocuments
      .mockReturnValueOnce(initialLoad.promise)
      .mockResolvedValueOnce({ items: [SAMPLE_DOC] })
    mockUploadDocument.mockResolvedValue(SAMPLE_DOC)

    renderWorkspace()

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await waitFor(() => {
      expect(screen.getByText('report.pdf')).toBeInTheDocument()
    })

    await act(async () => {
      initialLoad.reject(new Error('Stale network error'))
      await initialLoad.promise.catch(() => {})
    })

    expect(screen.getByText('report.pdf')).toBeInTheDocument()
    expect(screen.queryByText('Stale network error')).not.toBeInTheDocument()
  })
})

// ── Race safety: A->B navigation ─────────────────────────────────────────

describe('Race safety: projectId change (A -> B navigation)', () => {
  it('A list pending cannot contaminate B when projectId changes', async () => {
    type Resolve<T> = (value: T) => void
    function deferred<T>() {
      let resolve!: Resolve<T>
      const promise = new Promise<T>((r) => { resolve = r })
      return { promise, resolve }
    }

    const aLoad = deferred<{ items: typeof SAMPLE_DOC[] }>()
    const bDoc = { ...SAMPLE_DOC, id: 'd-b', originalFileName: 'b-file.pdf' }

    mockListDocuments
      .mockReturnValueOnce(aLoad.promise)
      .mockResolvedValueOnce({ items: [bDoc] })

    const { router } = renderWithRouter('/projects/proj-a')

    expect(screen.getByText('Loading documents…')).toBeInTheDocument()

    await act(async () => {
      await router.navigate('/projects/proj-b')
    })

    await waitFor(() => {
      expect(screen.getByText('b-file.pdf')).toBeInTheDocument()
    })

    await act(async () => {
      aLoad.resolve({ items: [SAMPLE_DOC] })
      await aLoad.promise
    })

    expect(screen.getByText('b-file.pdf')).toBeInTheDocument()
    expect(screen.queryByText('report.pdf')).not.toBeInTheDocument()
  })

  it('A upload pending cannot contaminate B state when projectId changes', async () => {
    type Resolve<T> = (value: T) => void
    function deferred<T>() {
      let resolve!: Resolve<T>
      const promise = new Promise<T>((r) => { resolve = r })
      return { promise, resolve }
    }

    const user = userEvent.setup()
    const aUpload = deferred<typeof SAMPLE_DOC>()
    const bDoc = { ...SAMPLE_DOC, id: 'd-b', originalFileName: 'b-file.pdf' }

    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValue({ items: [bDoc] })
    mockUploadDocument.mockReturnValue(aUpload.promise)

    const { router } = renderWithRouter('/projects/proj-a')

    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await act(async () => {
      await router.navigate('/projects/proj-b')
    })

    await waitFor(() => {
      expect(screen.getByText('b-file.pdf')).toBeInTheDocument()
    })

    await act(async () => {
      aUpload.resolve(SAMPLE_DOC)
      await aUpload.promise
    })

    expect(screen.getByText('b-file.pdf')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByText(/Upload succeeded/)).not.toBeInTheDocument()
  })

  it('upload A success after navigation to B cannot trigger A refresh into B', async () => {
    type Resolve<T> = (value: T) => void
    function deferred<T>() {
      let resolve!: Resolve<T>
      const promise = new Promise<T>((r) => { resolve = r })
      return { promise, resolve }
    }

    const user = userEvent.setup()
    const aUpload = deferred<typeof SAMPLE_DOC>()
    const aDoc = { ...SAMPLE_DOC, id: 'd-a', originalFileName: 'a-file.pdf' }
    const bDoc = { ...SAMPLE_DOC, id: 'd-b', originalFileName: 'b-file.pdf' }

    mockListDocuments
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValue({ items: [bDoc] })
    mockUploadDocument.mockReturnValue(aUpload.promise)

    const { router } = renderWithRouter('/projects/proj-a')

    await waitFor(() =>
      expect(screen.getByText('No documents yet. Upload one above.')).toBeInTheDocument(),
    )

    await user.upload(screen.getByLabelText('Choose file'), makePdf())
    await user.click(screen.getByRole('button', { name: 'Upload' }))

    await act(async () => {
      await router.navigate('/projects/proj-b')
    })

    await waitFor(() => {
      expect(screen.getByText('b-file.pdf')).toBeInTheDocument()
    })

    const listCallsBefore = mockListDocuments.mock.calls.length

    await act(async () => {
      aUpload.resolve(aDoc)
      await aUpload.promise
    })

    expect(screen.getByText('b-file.pdf')).toBeInTheDocument()
    expect(screen.queryByText('a-file.pdf')).not.toBeInTheDocument()
    expect(mockListDocuments.mock.calls.length).toBe(listCallsBefore)
  })
})
