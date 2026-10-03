import { useMemo, useRef, useState } from 'react'
import {
  askProjectQuestion,
  RagApiError,
  type AnswerProjectQuestionResponse,
  type GroundedCitationResponse,
} from './ragApi'
import type { DocumentResponse } from './documentsApi'
import './AskPanel.css'

type AskState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'answered'; response: AnswerProjectQuestionResponse }
  | { status: 'error'; message: string }

function describeAskError(err: unknown): string {
  if (err instanceof RagApiError) {
    switch (err.status) {
      case 400:
        return err.serverMessage ?? 'Invalid question. Please rephrase and try again.'
      case 502:
        return 'The AI service returned an unexpected response. Please try again.'
      case 503:
        return 'The AI service is currently unavailable. Please try again later.'
      default:
        return err.serverMessage ?? 'Something went wrong. Please try again.'
    }
  }
  return 'Something went wrong. Please try again.'
}

interface AskPanelProps {
  projectId: string
  documents: DocumentResponse[]
}

export function AskPanel({ projectId, documents }: AskPanelProps) {
  const [question, setQuestion] = useState('')
  const [askState, setAskState] = useState<AskState>({ status: 'idle' })
  const generationRef = useRef(0)

  const documentNames = useMemo(() => {
    const map = new Map<string, string>()
    for (const doc of documents) {
      map.set(doc.id, doc.originalFileName)
    }
    return map
  }, [documents])

  const loading = askState.status === 'loading'
  const canSubmit = question.trim().length > 0 && !loading

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const trimmed = question.trim()
    if (!trimmed || loading) return

    const gen = ++generationRef.current
    setAskState({ status: 'loading' })

    try {
      const response = await askProjectQuestion(projectId, trimmed)
      if (gen === generationRef.current) {
        setAskState({ status: 'answered', response })
      }
    } catch (err) {
      if (gen !== generationRef.current) return
      setAskState({ status: 'error', message: describeAskError(err) })
    }
  }

  if (documents.length === 0) {
    return (
      <div className="ask-panel">
        <h3 className="ask-heading">Ask OpsFlow</h3>
        <p className="ask-empty">
          No documents have been uploaded to this project yet.
          Upload documents in the Documents tab to enable grounded answers.
        </p>
      </div>
    )
  }

  return (
    <div className="ask-panel">
      <h3 className="ask-heading">Ask OpsFlow</h3>
      <p className="ask-description">
        Ask a question about this project&#39;s documents. Answers are
        grounded in uploaded content with source citations.
      </p>

      <form className="ask-form" onSubmit={handleSubmit}>
        <label className="ask-label" htmlFor="ask-question-input">
          Question
        </label>
        <textarea
          id="ask-question-input"
          className="ask-textarea"
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          placeholder="What would you like to know about this project's documents?"
          rows={3}
          disabled={loading}
          maxLength={2500}
        />
        <button
          type="submit"
          className="ask-submit"
          disabled={!canSubmit}
        >
          {loading ? 'Asking…' : 'Ask'}
        </button>
      </form>

      <div aria-live="polite" className="ask-results">
        {loading && (
          <p className="ask-loading" role="status">Generating answer…</p>
        )}

        {askState.status === 'error' && (
          <div className="ask-error" role="alert">
            {askState.message}
          </div>
        )}

        {askState.status === 'answered'
          && askState.response.status === 'insufficient_evidence' && (
          <div className="ask-insufficient" role="status">
            There is not enough evidence in the project documents to answer
            this question.
          </div>
        )}

        {askState.status === 'answered'
          && askState.response.status === 'answered'
          && askState.response.answer && (
          <>
            <div className="ask-answer">
              <h4 className="ask-answer-heading">Answer</h4>
              <p className="ask-answer-text">{askState.response.answer}</p>
            </div>

            {askState.response.citations.length > 0 && (
              <div className="ask-citations">
                <h4 className="ask-citations-heading">
                  Sources ({askState.response.citations.length})
                </h4>
                <ol className="ask-citation-list">
                  {askState.response.citations.map((citation, index) => (
                    <CitationCard
                      key={citation.documentChunkId}
                      citation={citation}
                      index={index + 1}
                      documentName={
                        documentNames.get(citation.documentId) ?? 'Document'
                      }
                    />
                  ))}
                </ol>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  )
}

function CitationCard({
  citation,
  index,
  documentName,
}: {
  citation: GroundedCitationResponse
  index: number
  documentName: string
}) {
  return (
    <li className="ask-citation-item">
      <p className="ask-citation-source">
        <span className="ask-citation-number">[{index}]</span>{' '}
        {documentName}
      </p>
      <p className="ask-citation-chunk">Chunk {citation.chunkIndex}</p>
      <blockquote className="ask-citation-text">{citation.text}</blockquote>
    </li>
  )
}
