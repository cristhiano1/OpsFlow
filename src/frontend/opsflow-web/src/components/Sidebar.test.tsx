import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect } from 'vitest'
import { Sidebar } from './Sidebar'

function renderSidebar(initialEntries: string[] = ['/'], showAnalytics = true) {
  return render(
    <MemoryRouter initialEntries={initialEntries}>
      <Sidebar showAnalytics={showAnalytics} />
    </MemoryRouter>,
  )
}

describe('Sidebar', () => {
  it('renders primary navigation landmark', () => {
    renderSidebar()
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument()
  })

  it('renders Dashboard link pointing to / for analytics roles', () => {
    renderSidebar()
    const link = screen.getByRole('link', { name: 'Dashboard' })
    expect(link).toBeInTheDocument()
    expect(link).toHaveAttribute('href', '/')
  })

  it('labels / as Home and hides Dashboard for non-analytics roles', () => {
    renderSidebar(['/'], false)
    expect(screen.getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/')
    expect(screen.queryByRole('link', { name: 'Dashboard' })).not.toBeInTheDocument()
  })

  it('marks Dashboard as active when location is /', () => {
    renderSidebar(['/'])
    const link = screen.getByRole('link', { name: 'Dashboard' })
    expect(link.className).toContain('active')
  })

  it('does not mark Dashboard as active on a different path', () => {
    renderSidebar(['/other'])
    const link = screen.getByRole('link', { name: 'Dashboard' })
    expect(link.className).not.toContain('active')
  })

  it('renders Projects link pointing to /projects', () => {
    renderSidebar()
    const link = screen.getByRole('link', { name: 'Projects' })
    expect(link).toBeInTheDocument()
    expect(link).toHaveAttribute('href', '/projects')
  })

  it('renders Projects link for non-analytics roles', () => {
    renderSidebar(['/'], false)
    expect(screen.getByRole('link', { name: 'Projects' })).toHaveAttribute('href', '/projects')
  })

  it('marks Projects as active when location is /projects', () => {
    renderSidebar(['/projects'])
    const link = screen.getByRole('link', { name: 'Projects' })
    expect(link.className).toContain('active')
  })

  it('does not mark Projects as active on a different path', () => {
    renderSidebar(['/'])
    const link = screen.getByRole('link', { name: 'Projects' })
    expect(link.className).not.toContain('active')
  })
})
