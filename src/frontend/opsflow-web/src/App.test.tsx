import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import App from './App'

describe('App', () => {
  it('renders the product name, subtitle and foundation message', () => {
    render(<App />)

    expect(
      screen.getByRole('heading', { level: 1, name: 'OpsFlow' }),
    ).toBeInTheDocument()

    expect(
      screen.getByText('Enterprise Work Management Platform'),
    ).toBeInTheDocument()

    expect(
      screen.getByText(/the application foundation is ready/i),
    ).toBeInTheDocument()
  })
})
