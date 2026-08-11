import { useCallback, useEffect, useRef, useState } from 'react'
import {
  createProject,
  listProjects,
  type ProjectResponse,
} from './projectsApi'
import './ProjectsPage.css'

type PageState =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'loaded'; projects: ProjectResponse[] }

export function ProjectsPage() {
  const [pageState, setPageState] = useState<PageState>({ status: 'loading' })
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)
  const loadGenerationRef = useRef(0)

  const invalidateLoads = useCallback(() => {
    ++loadGenerationRef.current
  }, [])

  const loadProjects = useCallback(async () => {
    const generation = ++loadGenerationRef.current
    try {
      const data = await listProjects()
      if (generation === loadGenerationRef.current) {
        setPageState({ status: 'loaded', projects: data.items })
      }
    } catch (err) {
      if (generation === loadGenerationRef.current) {
        setPageState({
          status: 'error',
          message: err instanceof Error ? err.message : 'Failed to load projects',
        })
      }
    }
  }, [])

  useEffect(() => {
    void loadProjects()
    return invalidateLoads
  }, [loadProjects, invalidateLoads])

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setFormError(null)
    setSubmitting(true)
    try {
      await createProject({
        name: name.trim(),
        description: description.trim() || null,
      })
      setName('')
      setDescription('')
      await loadProjects()
    } catch (err) {
      setFormError(
        err instanceof Error ? err.message : 'Failed to create project',
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="projects-page">
      <h2 className="projects-heading">Projects</h2>

      <form className="projects-form" onSubmit={handleSubmit}>
        <div className="projects-form-field">
          <label className="projects-form-label" htmlFor="project-name">
            Name
          </label>
          <input
            id="project-name"
            className="projects-form-input"
            type="text"
            value={name}
            onChange={e => setName(e.target.value)}
            maxLength={200}
            required
            disabled={submitting}
          />
        </div>
        <div className="projects-form-field">
          <label className="projects-form-label" htmlFor="project-description">
            Description
          </label>
          <textarea
            id="project-description"
            className="projects-form-textarea"
            value={description}
            onChange={e => setDescription(e.target.value)}
            maxLength={2000}
            disabled={submitting}
          />
        </div>
        {formError && <div className="projects-error">{formError}</div>}
        <div className="projects-form-actions">
          <button
            className="projects-form-submit"
            type="submit"
            disabled={submitting || !name.trim()}
          >
            {submitting ? 'Creating…' : 'Create project'}
          </button>
        </div>
      </form>

      {pageState.status === 'loading' && (
        <p className="projects-loading">Loading projects…</p>
      )}

      {pageState.status === 'error' && (
        <div className="projects-error">{pageState.message}</div>
      )}

      {pageState.status === 'loaded' && pageState.projects.length === 0 && (
        <p className="projects-empty">No projects yet. Create one above.</p>
      )}

      {pageState.status === 'loaded' && pageState.projects.length > 0 && (
        <ul className="projects-list">
          {pageState.projects.map(project => (
            <li key={project.id} className="projects-list-item">
              <p className="projects-item-name">{project.name}</p>
              {project.description && (
                <p className="projects-item-description">
                  {project.description}
                </p>
              )}
              <p className="projects-item-date">
                {new Date(project.createdAt).toLocaleDateString()}
              </p>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
