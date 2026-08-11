import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('./projectsApi', () => ({
  listProjects: vi.fn(),
  createProject: vi.fn(),
}))

import { listProjects, createProject } from './projectsApi'
import { ProjectsPage } from './ProjectsPage'

const mockListProjects = vi.mocked(listProjects)
const mockCreateProject = vi.mocked(createProject)

beforeEach(() => {
  mockListProjects.mockReset()
  mockCreateProject.mockReset()
})

describe('ProjectsPage', () => {
  it('shows loading state initially', () => {
    mockListProjects.mockReturnValue(new Promise(() => {}))
    render(<ProjectsPage />)
    expect(screen.getByText('Loading projects…')).toBeInTheDocument()
  })

  it('shows empty state when no projects', async () => {
    mockListProjects.mockResolvedValue({ items: [] })
    render(<ProjectsPage />)
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })
  })

  it('shows projects when loaded', async () => {
    mockListProjects.mockResolvedValue({
      items: [
        { id: 'p1', name: 'Alpha', description: 'First project', createdAt: '2026-08-10T00:00:00Z' },
      ],
    })
    render(<ProjectsPage />)
    await waitFor(() => {
      expect(screen.getByText('Alpha')).toBeInTheDocument()
    })
    expect(screen.getByText('First project')).toBeInTheDocument()
  })

  it('shows error state on load failure', async () => {
    mockListProjects.mockRejectedValue(new Error('Network error'))
    render(<ProjectsPage />)
    await waitFor(() => {
      expect(screen.getByText('Network error')).toBeInTheDocument()
    })
  })

  it('creates a project and refreshes list', async () => {
    const user = userEvent.setup()
    mockListProjects
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValueOnce({
        items: [
          { id: 'p1', name: 'New Project', description: null, createdAt: '2026-08-10T00:00:00Z' },
        ],
      })
    mockCreateProject.mockResolvedValue({
      id: 'p1', name: 'New Project', description: null, createdAt: '2026-08-10T00:00:00Z',
    })

    render(<ProjectsPage />)
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })

    await user.type(screen.getByLabelText('Name'), 'New Project')
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await waitFor(() => {
      expect(screen.getByText('New Project')).toBeInTheDocument()
    })
    expect(mockCreateProject).toHaveBeenCalledWith({
      name: 'New Project',
      description: null,
    })
  })

  it('shows form error when create fails', async () => {
    const user = userEvent.setup()
    mockListProjects.mockResolvedValue({ items: [] })
    mockCreateProject.mockRejectedValue(new Error('Validation failed'))

    render(<ProjectsPage />)
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })

    await user.type(screen.getByLabelText('Name'), 'Bad')
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    await waitFor(() => {
      expect(screen.getByText('Validation failed')).toBeInTheDocument()
    })
  })

  it('disables submit button while submitting', async () => {
    const user = userEvent.setup()
    mockListProjects.mockResolvedValue({ items: [] })
    mockCreateProject.mockReturnValue(new Promise(() => {}))

    render(<ProjectsPage />)
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })

    await user.type(screen.getByLabelText('Name'), 'Test')
    await user.click(screen.getByRole('button', { name: 'Create project' }))

    expect(screen.getByRole('button', { name: 'Creating…' })).toBeDisabled()
  })

  it('disables submit button when name is empty', async () => {
    mockListProjects.mockResolvedValue({ items: [] })
    render(<ProjectsPage />)
    await waitFor(() => {
      expect(screen.getByText('No projects yet. Create one above.')).toBeInTheDocument()
    })
    expect(screen.getByRole('button', { name: 'Create project' })).toBeDisabled()
  })

  it('renders the Projects heading', async () => {
    mockListProjects.mockResolvedValue({ items: [] })
    render(<ProjectsPage />)
    expect(screen.getByRole('heading', { name: 'Projects' })).toBeInTheDocument()
  })
})
