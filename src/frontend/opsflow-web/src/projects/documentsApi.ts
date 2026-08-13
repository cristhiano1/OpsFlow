import { apiFetch } from '../auth/apiClient'

export interface DocumentResponse {
  id: string
  originalFileName: string
  contentType: string
  sizeBytes: number
  createdAt: string
}

export interface DocumentListResponse {
  items: DocumentResponse[]
}

export class DocumentApiError extends Error {
  readonly status: number
  readonly serverMessage: string | null

  constructor(status: number, serverMessage: string | null) {
    super(
      serverMessage
        ? `Document API error ${status}: ${serverMessage}`
        : `Document API error ${status}`,
    )
    this.name = 'DocumentApiError'
    this.status = status
    this.serverMessage = serverMessage
  }
}

export class ProjectNotFoundError extends Error {
  constructor() {
    super('Project not found')
    this.name = 'ProjectNotFoundError'
  }
}

function safeTrimmed(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim()
  if (trimmed.length === 0 || trimmed.length > 500) return null
  return trimmed
}

async function extractSafeMessage(response: Response): Promise<string | null> {
  try {
    const contentType = response.headers.get('content-type') ?? ''
    if (contentType.includes('application/problem+json')) {
      const problem = (await response.json()) as {
        title?: string
        detail?: string
      }
      return safeTrimmed(problem.detail) ?? safeTrimmed(problem.title)
    }
    if (contentType.includes('text/plain')) {
      const text = await response.text()
      return safeTrimmed(text)
    }
    return null
  } catch {
    return null
  }
}

export async function listDocuments(
  projectId: string,
): Promise<DocumentListResponse> {
  const response = await apiFetch({
    path: `/api/v1/projects/${encodeURIComponent(projectId)}/documents`,
    method: 'GET',
  })
  if (response.ok) {
    return (await response.json()) as DocumentListResponse
  }
  if (response.status === 404) {
    throw new ProjectNotFoundError()
  }
  const message = await extractSafeMessage(response)
  throw new DocumentApiError(response.status, message)
}

export async function uploadDocument(
  projectId: string,
  file: File,
): Promise<DocumentResponse> {
  const formData = new FormData()
  formData.append('file', file)
  const response = await apiFetch({
    path: `/api/v1/projects/${encodeURIComponent(projectId)}/documents`,
    method: 'POST',
    body: formData,
  })
  if (response.ok) {
    return (await response.json()) as DocumentResponse
  }
  if (response.status === 404) {
    throw new ProjectNotFoundError()
  }
  const message = await extractSafeMessage(response)
  throw new DocumentApiError(response.status, message)
}
