import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import {
  DocumentApiError,
  listDocuments,
  ProjectNotFoundError,
  uploadDocument,
  type DocumentResponse,
} from './documentsApi'
import './ProjectWorkspacePage.css'

const ALLOWED_EXTENSIONS = new Set(['.pdf', '.txt', '.docx'])
const MAX_FILE_SIZE = 25 * 1024 * 1024

type PageState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'loaded'; documents: DocumentResponse[] }
  | { status: 'not-found' }

type UploadState =
  | { status: 'idle' }
  | { status: 'uploading'; fileName: string }
  | { status: 'error'; message: string }
  | { status: 'success-refresh-failed' }

function getFileExtension(name: string): string {
  const dot = name.lastIndexOf('.')
  if (dot === -1) return ''
  return name.slice(dot).toLowerCase()
}

function formatFileSize(bytes: number): string {
  if (bytes < 1000) return `${bytes} B`
  if (bytes < 1_000_000) return `${(bytes / 1000).toFixed(1)} KB`
  return `${(bytes / 1_000_000).toFixed(1)} MB`
}

function formatContentType(contentType: string): string {
  switch (contentType) {
    case 'application/pdf':
      return 'PDF'
    case 'text/plain':
      return 'Text'
    case 'application/vnd.openxmlformats-officedocument.wordprocessingml.document':
      return 'Word document'
    default:
      return 'Document'
  }
}

function formatDate(iso: string): string {
  const d = new Date(iso)
  const year = d.getUTCFullYear()
  const month = String(d.getUTCMonth() + 1).padStart(2, '0')
  const day = String(d.getUTCDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function validateFile(file: File): string | null {
  if (file.size === 0) return 'File is empty.'
  if (file.size > MAX_FILE_SIZE) return 'File exceeds the 25 MiB limit.'
  const ext = getFileExtension(file.name)
  if (!ALLOWED_EXTENSIONS.has(ext)) {
    return 'Only PDF, TXT, and DOCX files are supported.'
  }
  return null
}

function describeUploadError(err: unknown): string {
  if (err instanceof DocumentApiError) {
    switch (err.status) {
      case 400:
        return err.serverMessage ?? 'The file could not be validated.'
      case 413:
        return 'The file exceeds the server size limit.'
      case 422:
        return err.serverMessage ?? 'The file type is not supported.'
      default:
        return err.serverMessage ?? 'Upload failed. Please try again.'
    }
  }
  if (err instanceof ProjectNotFoundError) {
    return 'This project was not found.'
  }
  return 'Upload failed. Please try again.'
}

export function ProjectWorkspacePage() {
  const { projectId } = useParams<{ projectId: string }>()
  const [pageState, setPageState] = useState<PageState>({ status: 'loading' })
  const [uploadState, setUploadState] = useState<UploadState>({ status: 'idle' })
  const [clientError, setClientError] = useState<string | null>(null)
  const [selectedFile, setSelectedFile] = useState<File | null>(null)
  const generationRef = useRef(0)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const invalidateOperations = useCallback(() => {
    ++generationRef.current
  }, [])

  const loadDocuments = useCallback(
    async (gen: number) => {
      if (!projectId) return
      try {
        const data = await listDocuments(projectId)
        if (gen === generationRef.current) {
          setPageState({ status: 'loaded', documents: data.items })
        }
      } catch (err) {
        if (gen !== generationRef.current) return
        if (err instanceof ProjectNotFoundError) {
          setPageState({ status: 'not-found' })
        } else {
          setPageState({
            status: 'error',
            message: err instanceof Error ? err.message : 'Failed to load documents',
          })
        }
      }
    },
    [projectId],
  )

  useEffect(() => {
    const gen = ++generationRef.current
    setPageState({ status: 'loading' })
    setUploadState({ status: 'idle' })
    setClientError(null)
    setSelectedFile(null)
    void loadDocuments(gen)
    return invalidateOperations
  }, [projectId, loadDocuments, invalidateOperations])

  function handleRetryLoad() {
    const gen = ++generationRef.current
    setPageState({ status: 'loading' })
    void loadDocuments(gen)
  }

  function handleFileChange(e: React.ChangeEvent<HTMLInputElement>) {
    setClientError(null)
    setSelectedFile(e.target.files?.[0] ?? null)
  }

  async function handleUpload(e: React.FormEvent) {
    e.preventDefault()
    const file = selectedFile
    if (!file || !projectId) return

    const validationError = validateFile(file)
    if (validationError) {
      setClientError(validationError)
      return
    }

    setClientError(null)
    const capturedGen = generationRef.current
    setUploadState({ status: 'uploading', fileName: file.name })

    try {
      await uploadDocument(projectId, file)
    } catch (err) {
      if (capturedGen !== generationRef.current) return
      setUploadState({ status: 'error', message: describeUploadError(err) })
      return
    }

    if (capturedGen !== generationRef.current) return

    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
    setSelectedFile(null)

    const refreshGen = ++generationRef.current
    setUploadState({ status: 'idle' })
    try {
      const data = await listDocuments(projectId)
      if (refreshGen === generationRef.current) {
        setPageState({ status: 'loaded', documents: data.items })
      }
    } catch {
      if (refreshGen === generationRef.current) {
        setUploadState({ status: 'success-refresh-failed' })
      }
    }
  }

  function handleRetryRefresh() {
    const gen = ++generationRef.current
    setUploadState({ status: 'idle' })
    setPageState({ status: 'loading' })
    void loadDocuments(gen)
  }

  if (!projectId) return null

  const uploading = uploadState.status === 'uploading'

  return (
    <div className="workspace-page">
      <nav className="workspace-breadcrumb" aria-label="Breadcrumb">
        <Link to="/projects" className="workspace-back-link">
          Projects
        </Link>
        <span className="workspace-breadcrumb-sep" aria-hidden="true">
          /
        </span>
        <span>Documents</span>
      </nav>

      <h2 className="workspace-heading">Documents</h2>

      <form className="workspace-upload-form" onSubmit={handleUpload}>
        <div className="workspace-upload-field">
          <label className="workspace-upload-label" htmlFor="document-file-input">
            Choose file
          </label>
          <input
            id="document-file-input"
            ref={fileInputRef}
            type="file"
            accept=".pdf,.txt,.docx"
            className="workspace-upload-input"
            disabled={uploading}
            onChange={handleFileChange}
          />
        </div>
        {clientError && (
          <div className="workspace-upload-client-error" role="alert">
            {clientError}
          </div>
        )}
        <button
          type="submit"
          className="workspace-upload-submit"
          disabled={!selectedFile || uploading}
        >
          {uploading ? `Uploading ${uploadState.fileName}…` : 'Upload'}
        </button>
      </form>

      <div aria-live="polite" className="workspace-upload-status">
        {uploading && (
          <p className="workspace-status-msg">
            Uploading {uploadState.fileName}…
          </p>
        )}
        {uploadState.status === 'error' && (
          <div className="workspace-upload-error" role="alert">
            {uploadState.message}
          </div>
        )}
        {uploadState.status === 'success-refresh-failed' && (
          <div className="workspace-upload-warn">
            Upload succeeded, but the document list could not be refreshed.
            <button
              type="button"
              className="workspace-retry-button"
              onClick={handleRetryRefresh}
            >
              Retry
            </button>
          </div>
        )}
      </div>

      {pageState.status === 'loading' && (
        <p className="workspace-loading">Loading documents…</p>
      )}

      {pageState.status === 'error' && (
        <div className="workspace-error">
          <p>{pageState.message}</p>
          <button
            type="button"
            className="workspace-retry-button"
            onClick={handleRetryLoad}
          >
            Retry
          </button>
        </div>
      )}

      {pageState.status === 'not-found' && (
        <div className="workspace-not-found">
          <p>Project not found.</p>
          <Link to="/projects" className="workspace-back-link">
            Back to Projects
          </Link>
        </div>
      )}

      {pageState.status === 'loaded' && pageState.documents.length === 0 && (
        <p className="workspace-empty">No documents yet. Upload one above.</p>
      )}

      {pageState.status === 'loaded' && pageState.documents.length > 0 && (
        <ul className="workspace-document-list">
          {pageState.documents.map((doc) => (
            <li key={doc.id} className="workspace-document-item">
              <p className="workspace-document-name">{doc.originalFileName}</p>
              <p className="workspace-document-meta">
                <span className="workspace-document-type">
                  {formatContentType(doc.contentType)}
                </span>
                <span className="workspace-document-sep" aria-hidden="true">·</span>
                {formatFileSize(doc.sizeBytes)}
                <span className="workspace-document-sep" aria-hidden="true">·</span>
                <time dateTime={doc.createdAt}>{formatDate(doc.createdAt)}</time>
              </p>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
