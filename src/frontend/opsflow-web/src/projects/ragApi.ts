import { apiFetch } from '../auth/apiClient'
import { ProjectNotFoundError } from './documentsApi'

export interface GroundedCitationResponse {
  documentId: string
  documentChunkId: string
  chunkIndex: number
  startOffset: number
  endOffset: number
  text: string
}

export interface AnswerProjectQuestionResponse {
  status: string
  answer: string | null
  citations: GroundedCitationResponse[]
}

export interface SearchDocumentChunkHitResponse {
  documentId: string
  documentChunkId: string
  chunkIndex: number
  startOffset: number
  endOffset: number
  text: string
}

export interface SearchDocumentChunksResponse {
  items: SearchDocumentChunkHitResponse[]
}

export class RagApiError extends Error {
  readonly status: number
  readonly serverMessage: string | null

  constructor(status: number, serverMessage: string | null) {
    super(
      serverMessage
        ? `RAG API error ${status}: ${serverMessage}`
        : `RAG API error ${status}`,
    )
    this.name = 'RagApiError'
    this.status = status
    this.serverMessage = serverMessage
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

export async function askProjectQuestion(
  projectId: string,
  question: string,
): Promise<AnswerProjectQuestionResponse> {
  const response = await apiFetch({
    path: `/api/v1/projects/${encodeURIComponent(projectId)}/answer`,
    method: 'POST',
    body: JSON.stringify({ question }),
    headers: { 'Content-Type': 'application/json' },
  })
  if (response.ok) {
    return (await response.json()) as AnswerProjectQuestionResponse
  }
  if (response.status === 404) {
    throw new ProjectNotFoundError()
  }
  const message = await extractSafeMessage(response)
  throw new RagApiError(response.status, message)
}

export async function searchProjectDocuments(
  projectId: string,
  query: string,
  topK?: number,
): Promise<SearchDocumentChunksResponse> {
  const body: { queryText: string; topK?: number } = { queryText: query }
  if (topK !== undefined) {
    body.topK = topK
  }
  const response = await apiFetch({
    path: `/api/v1/projects/${encodeURIComponent(projectId)}/search`,
    method: 'POST',
    body: JSON.stringify(body),
    headers: { 'Content-Type': 'application/json' },
  })
  if (response.ok) {
    return (await response.json()) as SearchDocumentChunksResponse
  }
  if (response.status === 404) {
    throw new ProjectNotFoundError()
  }
  const message = await extractSafeMessage(response)
  throw new RagApiError(response.status, message)
}
